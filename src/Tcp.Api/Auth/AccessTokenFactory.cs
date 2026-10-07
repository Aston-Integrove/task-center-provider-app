using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Tcp.Api.Auth;

public sealed record IssuedToken(string AccessToken, int ExpiresIn, string Scope);

/// <summary>Creates our RS256 access tokens (FR-TOK-04).</summary>
public sealed class AccessTokenFactory(ISigningKeyProvider keys, IOptions<OAuthOptions> options, TimeProvider time)
{
    private readonly JsonWebTokenHandler _handler = new();

    public IssuedToken Issue(
        OAuthClient client,
        string subject,
        IReadOnlyCollection<string> scopes,
        int lifetimeSeconds,
        IReadOnlyDictionary<string, object>? extraClaims = null)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var scope = string.Join(' ', scopes);

        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["client_id"] = client.ClientId,
            ["scope"] = scope,
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        if (extraClaims is not null)
            foreach (var (k, v) in extraClaims) claims[k] = v;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Value.Issuer,
            Audience = client.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddSeconds(lifetimeSeconds),
            SigningCredentials = keys.Current,
            Claims = claims,
        };

        return new IssuedToken(_handler.CreateToken(descriptor), lifetimeSeconds, scope);
    }
}
