namespace Tcp.Api.Diagnostics;

public sealed record RequestLogEntry(
    DateTimeOffset Timestamp,
    string CorrelationId,
    string Method,
    string Path,
    string Query,
    int Status,
    long DurationMs,
    string? ClientId,
    IReadOnlyDictionary<string, string> RequestHeaders,
    string RequestBody,
    IReadOnlyDictionary<string, string> ResponseHeaders,
    string ResponseBody);

public interface IRequestLog
{
    void Add(RequestLogEntry entry);

    /// <summary>Newest first, optionally filtered by path prefix and exact status.</summary>
    IReadOnlyList<RequestLogEntry> Snapshot(string? pathPrefix = null, int? status = null, int? limit = null);
}

/// <summary>Fixed-capacity in-memory ring buffer (constitution VIII).</summary>
public sealed class RequestLogBuffer(int capacity) : IRequestLog
{
    private readonly RequestLogEntry?[] _items = new RequestLogEntry?[Math.Max(1, capacity)];
    private readonly Lock _gate = new();
    private int _next;
    private int _count;

    public int Capacity => _items.Length;

    public void Add(RequestLogEntry entry)
    {
        lock (_gate)
        {
            _items[_next] = entry;
            _next = (_next + 1) % _items.Length;
            if (_count < _items.Length) _count++;
        }
    }

    public IReadOnlyList<RequestLogEntry> Snapshot(string? pathPrefix = null, int? status = null, int? limit = null)
    {
        RequestLogEntry[] copy;
        lock (_gate)
        {
            copy = new RequestLogEntry[_count];
            for (var i = 0; i < _count; i++)
            {
                var idx = (_next - 1 - i + _items.Length * 2) % _items.Length; // newest first
                copy[i] = _items[idx]!;
            }
        }

        IEnumerable<RequestLogEntry> q = copy;
        if (!string.IsNullOrEmpty(pathPrefix))
            q = q.Where(e => e.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase));
        if (status is { } s)
            q = q.Where(e => e.Status == s);
        if (limit is { } l and > 0)
            q = q.Take(l);
        return q.ToList();
    }
}
