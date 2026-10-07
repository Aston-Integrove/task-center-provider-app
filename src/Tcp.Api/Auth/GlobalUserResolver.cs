using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Tcp.Domain.Identity;

namespace Tcp.Api.Auth;

public sealed record ResolvedUser(DirectoryUser User, string Via);

public interface IGlobalUserResolver
{
    /// <summary>Resolves the acting user from assertion claims (FR-PP-04); null if unknown or inactive.</summary>
    Task<ResolvedUser?> ResolveAsync(JsonObject claims, CancellationToken ct);

    /// <summary>Resolves a candidate Global User ID (e.g. a SAML NameID); null if unknown or inactive.</summary>
    Task<ResolvedUser?> ResolveByIdAsync(string candidate, CancellationToken ct);
}

/// <summary>
/// Claim lookup order from <c>OAuth:UserIdClaims</c> (dotted path = nested claim), every candidate must be a
/// GUID that exists in the SCIM store; falls back to a unique e-mail match. The user must be active.
/// </summary>
public sealed class GlobalUserResolver(
    IUserDirectory directory,
    IOptions<OAuthOptions> options,
    ILogger<GlobalUserResolver> logger) : IGlobalUserResolver
{
    public async Task<ResolvedUser?> ResolveAsync(JsonObject claims, CancellationToken ct)
    {
        foreach (var path in options.Value.EffectiveUserIdClaims)
        {
            var candidate = ReadString(claims, path);
            if (candidate is null) continue;

            var resolved = await ResolveByIdAsync(candidate, ct);
            if (resolved is not null) return resolved with { Via = path };
        }

        var email = ReadString(claims, "email");
        if (!string.IsNullOrWhiteSpace(email))
        {
            var byEmail = await directory.FindUniqueByEmailAsync(email, ct);
            if (byEmail is { Active: true }) return new ResolvedUser(byEmail, "email");
            if (byEmail is not null) logger.LogWarning("Assertion user matched by e-mail is inactive");
        }

        logger.LogWarning("Could not resolve a Global User ID; claim names present: {ClaimNames}",
            string.Join(',', claims.Select(c => c.Key)));
        return null;
    }

    public async Task<ResolvedUser?> ResolveByIdAsync(string candidate, CancellationToken ct)
    {
        if (!GlobalUserId.TryNormalize(candidate, out var id)) return null;
        var user = await directory.FindByGlobalUserIdAsync(id, ct);
        if (user is null) return null;
        if (!user.Active)
        {
            logger.LogWarning("Resolved user {GlobalUserId} is inactive", id);
            return null;
        }
        return new ResolvedUser(user, "id");
    }

    /// <summary>Reads a claim by exact name first (names may contain dots), then as a nested path.</summary>
    internal static string? ReadString(JsonObject claims, string path)
    {
        if (claims.TryGetPropertyValue(path, out var direct) && AsString(direct) is { } s) return s;

        JsonNode? node = claims;
        foreach (var segment in path.Split('.'))
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(segment, out node)) return null;
        }
        return AsString(node);
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
