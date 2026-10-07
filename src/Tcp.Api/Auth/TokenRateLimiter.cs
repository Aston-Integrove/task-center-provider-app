using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Tcp.Api.Auth;

/// <summary>Fixed-window limiter per client_id (FR-TOK-06); idle partitions are reclaimed automatically.</summary>
public sealed class TokenRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public TokenRateLimiter(IOptions<OAuthOptions> options)
    {
        var permits = Math.Max(1, options.Value.TokenRateLimitPerMinute);
        _limiter = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    }

    public bool TryAcquire(string key)
    {
        using var lease = _limiter.AttemptAcquire(key);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
