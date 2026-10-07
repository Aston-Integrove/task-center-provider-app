using System.Text.RegularExpressions;

namespace Tcp.Api.Diagnostics;

/// <summary>Accepts an inbound <c>X-Correlation-Id</c> (if sane) or mints one, echoes it, and scopes logs with it.</summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "tcp.correlationId";

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,64}$")]
    private static partial Regex SafeId();

    public async Task InvokeAsync(HttpContext context)
    {
        var inbound = context.Request.Headers[HeaderName].FirstOrDefault();
        var id = inbound is not null && SafeId().IsMatch(inbound) ? inbound : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = id;
        context.Response.Headers[HeaderName] = id;
        // The exception handler clears headers; re-assert just before the response starts.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["correlationId"] = id }))
        {
            await next(context);
        }
    }

    public static string? Get(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var v) ? v as string : null;
}
