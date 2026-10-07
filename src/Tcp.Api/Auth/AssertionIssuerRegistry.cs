using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Tcp.Api.Auth;

public interface IAssertionIssuerRegistry
{
    TrustedAssertionIssuer? Find(string issuer);

    /// <summary>Signing keys of a trusted issuer; cached 1 h. <paramref name="forceRefresh"/> bypasses the cache (rate-limited).</summary>
    Task<IReadOnlyList<SecurityKey>> GetKeysAsync(TrustedAssertionIssuer issuer, bool forceRefresh, CancellationToken ct);
}

/// <summary>
/// Trusted assertion issuers and their JWKS (FR-PP-02). Only issuers listed in configuration are ever
/// contacted, so a token cannot make us fetch an arbitrary URL.
/// </summary>
public sealed class AssertionIssuerRegistry(
    IOptions<OAuthOptions> options,
    IHttpClientFactory httpClientFactory,
    TimeProvider time,
    ILogger<AssertionIssuerRegistry> logger) : IAssertionIssuerRegistry
{
    public const string HttpClientName = "assertion-issuers";
    private const string XsuaaTokenSuffix = "/oauth/token";
    private const int MaxResponseBytes = 256 * 1024;

    public static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    public static readonly TimeSpan MinForcedRefreshInterval = TimeSpan.FromSeconds(30);

    private sealed class State
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public string? JwksUri;
        public IReadOnlyList<SecurityKey>? Keys;
        public DateTimeOffset FetchedAt;
        public DateTimeOffset LastForcedAt = DateTimeOffset.MinValue;
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);

    public TrustedAssertionIssuer? Find(string issuer) =>
        options.Value.TrustedAssertionIssuers.FirstOrDefault(i =>
            string.Equals(i.Issuer, issuer, StringComparison.Ordinal));

    public async Task<IReadOnlyList<SecurityKey>> GetKeysAsync(TrustedAssertionIssuer issuer, bool forceRefresh, CancellationToken ct)
    {
        var state = _states.GetOrAdd(issuer.Issuer, _ => new State());
        await state.Gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            var fresh = state.Keys is not null && now - state.FetchedAt < CacheDuration;
            var mayForce = forceRefresh && now - state.LastForcedAt >= MinForcedRefreshInterval;

            if (fresh && !mayForce) return state.Keys!;
            if (forceRefresh) state.LastForcedAt = now;

            try
            {
                state.JwksUri ??= await ResolveJwksUriAsync(issuer, ct);
                state.Keys = await FetchKeysAsync(state.JwksUri, ct);
                state.FetchedAt = now;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Could not load JWKS for trusted issuer {Issuer}", issuer.Issuer);
                if (state.Keys is null) throw;
                // Serve stale keys rather than failing every login during a transient outage.
            }
            return state.Keys!;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    internal static string DeriveXsuaaJwksUri(string issuer)
    {
        var baseUrl = issuer.EndsWith(XsuaaTokenSuffix, StringComparison.Ordinal)
            ? issuer[..^XsuaaTokenSuffix.Length]
            : issuer.TrimEnd('/');
        return baseUrl + "/token_keys";
    }

    private async Task<string> ResolveJwksUriAsync(TrustedAssertionIssuer issuer, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(issuer.JwksUri)) return issuer.JwksUri;
        if (issuer.Type == AssertionIssuerType.Xsuaa) return DeriveXsuaaJwksUri(issuer.Issuer);

        var discoveryUrl = issuer.Issuer.TrimEnd('/') + "/.well-known/openid-configuration";
        var json = await GetStringAsync(discoveryUrl, ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("jwks_uri", out var uri) && uri.GetString() is { Length: > 0 } s)
            return s;
        throw new InvalidOperationException($"OIDC discovery for {issuer.Issuer} has no jwks_uri");
    }

    private async Task<IReadOnlyList<SecurityKey>> FetchKeysAsync(string jwksUri, CancellationToken ct)
    {
        var json = await GetStringAsync(jwksUri, ct);
        var set = new JsonWebKeySet(json);
        return set.GetSigningKeys().ToList();
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidOperationException("JWKS response too large");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > MaxResponseBytes) throw new InvalidOperationException("JWKS response too large");
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
