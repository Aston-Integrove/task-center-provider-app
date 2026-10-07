using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;

namespace Tcp.Api.Auth;

public sealed record DecodedAssertion(JsonObject Header, JsonObject Claims);

/// <summary>
/// Decodes a JWT's header and payload WITHOUT validating or returning the signature. Used by the admin
/// diagnostics endpoint to inspect what BTP actually sends (spike S-02).
/// </summary>
public static class AssertionDecoder
{
    public static DecodedAssertion? TryDecode(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Trim().Split('.');
        if (parts.Length != 3) return null;

        try
        {
            var header = JsonNode.Parse(Base64UrlEncoder.Decode(parts[0])) as JsonObject;
            var claims = JsonNode.Parse(Base64UrlEncoder.Decode(parts[1])) as JsonObject;
            return header is null || claims is null ? null : new DecodedAssertion(header, claims);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
