namespace Tcp.Api.Auth.Grants;

public sealed record GrantContext(
    OAuthClient Client,
    IFormCollection Form,
    IReadOnlyList<string> Scopes,
    CancellationToken Ct);

public sealed class GrantResult
{
    public string? Subject { get; private init; }
    public int LifetimeSeconds { get; private init; }
    public IReadOnlyDictionary<string, object>? ExtraClaims { get; private init; }
    public string? Error { get; private init; }
    public string? ErrorDescription { get; private init; }
    public bool Succeeded => Error is null;

    public static GrantResult Ok(string subject, int lifetimeSeconds, IReadOnlyDictionary<string, object>? extraClaims = null) =>
        new() { Subject = subject, LifetimeSeconds = lifetimeSeconds, ExtraClaims = extraClaims };

    public static GrantResult Fail(string error, string? description = null) =>
        new() { Error = error, ErrorDescription = description };

    public static GrantResult InvalidGrant(string description) => Fail("invalid_grant", description);
}

/// <summary>One implementation per <c>grant_type</c>; pluggable so SAML can be added without touching the endpoint.</summary>
public interface IGrantHandler
{
    string GrantType { get; }
    Task<GrantResult> HandleAsync(GrantContext context);
}
