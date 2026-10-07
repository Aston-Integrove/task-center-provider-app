using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using Tcp.Api.Configuration;

namespace Tcp.Api.Diagnostics;

/// <summary>
/// Emits one structured log line per request and, for integration paths, records headers and
/// (redacted, truncated) bodies in the ring buffer so we can see what SAP actually sends.
/// </summary>
public sealed class RequestLogMiddleware(
    RequestDelegate next,
    IRequestLog buffer,
    IOptions<DiagnosticsOptions> options,
    ILogger<RequestLogMiddleware> logger)
{
    public const int MaxBodyBytes = 8 * 1024;
    private const int MaxCaptureBytes = 64 * 1024;

    /// <summary>Only integration traffic is kept in the buffer; the admin UI and health probes would drown it.</summary>
    public static readonly string[] CapturedPrefixes = ["/oauth", "/scim", "/task-provider", "/api/task-provider"];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var capture = options.Value.RequestLogEnabled &&
                      CapturedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        var sw = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        var requestBody = string.Empty;
        MemoryStream? responseTee = null;
        Stream? originalBody = null;

        if (capture)
        {
            context.Request.EnableBuffering();
            requestBody = await ReadRequestBodyAsync(context.Request);
            originalBody = context.Response.Body;
            responseTee = new MemoryStream();
            context.Response.Body = new TeeStream(originalBody, responseTee, MaxCaptureBytes);
        }

        try
        {
            await next(context);
        }
        finally
        {
            sw.Stop();
            if (originalBody is not null) context.Response.Body = originalBody;

            var correlationId = CorrelationIdMiddleware.Get(context) ?? string.Empty;
            var clientId = ResolveClientId(context);
            var status = context.Response.StatusCode;

            logger.LogInformation(
                "request {Method} {Route} -> {Status} in {DurationMs} ms (client {ClientId}, correlation {CorrelationId})",
                context.Request.Method, path, status, sw.ElapsedMilliseconds, clientId ?? "-", correlationId);

            if (capture)
            {
                var responseBody = responseTee is null
                    ? string.Empty
                    : Encoding.UTF8.GetString(responseTee.GetBuffer(), 0, (int)responseTee.Length);

                buffer.Add(new RequestLogEntry(
                    started, correlationId, context.Request.Method, path,
                    Redactor.RedactQuery(context.Request.QueryString.Value),
                    status, sw.ElapsedMilliseconds, clientId,
                    RedactHeaders(context.Request.Headers),
                    Truncate(Redactor.RedactBody(requestBody, context.Request.ContentType)),
                    RedactHeaders(context.Response.Headers),
                    Truncate(Redactor.RedactBody(responseBody, context.Response.ContentType))));
            }
        }
    }

    private static async Task<string> ReadRequestBodyAsync(HttpRequest request)
    {
        if (request.ContentLength is 0) return string.Empty;
        var buf = new byte[MaxCaptureBytes];
        var total = 0;
        int read;
        while (total < buf.Length &&
               (read = await request.Body.ReadAsync(buf.AsMemory(total, buf.Length - total))) > 0)
            total += read;
        request.Body.Position = 0;
        return Encoding.UTF8.GetString(buf, 0, total);
    }

    private static Dictionary<string, string> RedactHeaders(IHeaderDictionary headers) =>
        headers.ToDictionary(h => h.Key, h => Redactor.RedactHeader(h.Key, h.Value.ToString()),
            StringComparer.OrdinalIgnoreCase);

    internal static string Truncate(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        return bytes.Length <= MaxBodyBytes
            ? s
            : Encoding.UTF8.GetString(bytes, 0, MaxBodyBytes) + "...[truncated]";
    }

    private static string? ResolveClientId(HttpContext context) =>
        context.User.FindFirst("client_id")?.Value ?? context.User.FindFirst("sub")?.Value;
}

/// <summary>Write-through stream that keeps a bounded copy of what was written.</summary>
internal sealed class TeeStream(Stream inner, MemoryStream copy, int maxCopy) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        Capture(buffer.AsSpan(offset, count));
        inner.Write(buffer, offset, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Capture(buffer.Span);
        await inner.WriteAsync(buffer, cancellationToken);
    }

    private void Capture(ReadOnlySpan<byte> data)
    {
        var room = maxCopy - (int)copy.Length;
        if (room > 0) copy.Write(data[..Math.Min(room, data.Length)]);
    }
}
