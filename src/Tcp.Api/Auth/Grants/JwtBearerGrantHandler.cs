using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Tcp.Api.Configuration;

namespace Tcp.Api.Auth.Grants;

/// <summary>
/// RFC 7523 JWT bearer grant (principal propagation): validates the user's BTP/IAS JWT against the issuer's
/// JWKS and mints our token whose <c>sub</c> is the user's Global User ID.
/// </summary>
public sealed class JwtBearerGrantHandler(
    IAssertionIssuerRegistry issuers,
    IGlobalUserResolver resolver,
    TimeProvider time,
    IOptions<DiagnosticsOptions> diagnostics,
    ILogger<JwtBearerGrantHandler> logger) : IGrantHandler
{
    public const int MaxAssertionChars = 16 * 1024;
    public const int MaxLifetimeSeconds = 600;
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    private static readonly string[] AllowedAlgorithms =
        [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512, SecurityAlgorithms.EcdsaSha256];

    private readonly JsonWebTokenHandler _handler = new();

    public string GrantType => GrantTypes.JwtBearer;

    public async Task<GrantResult> HandleAsync(GrantContext context)
    {
        var assertion = context.Form["assertion"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(assertion))
            return GrantResult.Fail("invalid_request", "assertion is required");
        if (assertion.Length > MaxAssertionChars)
            return GrantResult.Fail("invalid_request", "assertion too large");

        JsonWebToken unvalidated;
        try
        {
            unvalidated = new JsonWebToken(assertion);
        }
        catch (ArgumentException)
        {
            return GrantResult.InvalidGrant("assertion is not a JWT");
        }

        if (!AllowedAlgorithms.Contains(unvalidated.Alg, StringComparer.Ordinal))
            return GrantResult.InvalidGrant("unsupported signing algorithm");

        var issuer = unvalidated.Issuer;
        var trusted = string.IsNullOrEmpty(issuer) ? null : issuers.Find(issuer);
        if (trusted is null)
        {
            logger.LogWarning("Assertion from untrusted issuer {Issuer}", Truncate(issuer));
            return GrantResult.InvalidGrant("untrusted issuer");
        }

        var keys = await issuers.GetKeysAsync(trusted, forceRefresh: false, context.Ct);
        // Key rotation: an unknown kid triggers one (rate-limited) refresh before we give up.
        if (!string.IsNullOrEmpty(unvalidated.Kid) && keys.All(k => k.KeyId != unvalidated.Kid))
            keys = await issuers.GetKeysAsync(trusted, forceRefresh: true, context.Ct);

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = trusted.Issuer,
            IssuerSigningKeys = keys,
            ValidateIssuer = true,
            ValidateAudience = trusted.ValidateAudience,
            ValidAudiences = trusted.ValidAudiences,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = AllowedAlgorithms,
            ClockSkew = ClockSkew,
        };

        var result = await _handler.ValidateTokenAsync(assertion, parameters);
        if (!result.IsValid || result.SecurityToken is not JsonWebToken validated)
        {
            logger.LogWarning("Assertion validation failed for issuer {Issuer}: {Reason}",
                trusted.Issuer, result.Exception?.GetType().Name ?? "unknown");
            return GrantResult.InvalidGrant("assertion validation failed");
        }

        var payload = ReadPayload(validated);
        var user = await resolver.ResolveAsync(payload, context.Ct);

        if (diagnostics.Value.LogAssertionClaims)
        {
            logger.LogInformation(
                "Assertion claims from {Issuer}: names [{ClaimNames}] -> user {GlobalUserId} via {Via}",
                trusted.Issuer, string.Join(',', payload.Select(p => p.Key)),
                user?.User.GlobalUserId ?? "(unresolved)", user?.Via ?? "-");
        }

        if (user is null) return GrantResult.InvalidGrant("unknown user");

        var secondsLeft = (int)Math.Floor((validated.ValidTo - time.GetUtcNow().UtcDateTime).TotalSeconds);
        var lifetime = Math.Min(Math.Min(context.Client.TokenLifetimeSeconds, MaxLifetimeSeconds), secondsLeft);
        if (lifetime < 1) return GrantResult.InvalidGrant("assertion expires too soon");

        var extra = new Dictionary<string, object> { ["orig_iss"] = trusted.Issuer };
        if (!string.IsNullOrEmpty(user.User.Email)) extra["email"] = user.User.Email;
        if (!string.IsNullOrEmpty(user.User.DisplayName)) extra["name"] = user.User.DisplayName;

        return GrantResult.Ok(user.User.GlobalUserId, lifetime, extra);
    }

    internal static JsonObject ReadPayload(JsonWebToken token) =>
        JsonNode.Parse(Base64UrlEncoder.Decode(token.EncodedPayload)) as JsonObject ?? [];

    private static string Truncate(string? s) => s is null ? "(none)" : s.Length <= 200 ? s : s[..200];
}
