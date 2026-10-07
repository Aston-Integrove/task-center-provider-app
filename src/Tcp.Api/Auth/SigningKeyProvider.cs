using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;

namespace Tcp.Api.Auth;

public sealed record PublicJwk(
    [property: JsonPropertyName("kty")] string Kty,
    [property: JsonPropertyName("kid")] string Kid,
    [property: JsonPropertyName("use")] string Use,
    [property: JsonPropertyName("alg")] string Alg,
    [property: JsonPropertyName("n")] string N,
    [property: JsonPropertyName("e")] string E);

public interface ISigningKeyProvider
{
    /// <summary>Key used to sign newly issued tokens.</summary>
    SigningCredentials Current { get; }

    /// <summary>Current key first, then the previous key (rotation window).</summary>
    IReadOnlyList<RsaSecurityKey> ValidationKeys { get; }

    IReadOnlyList<PublicJwk> PublicKeys { get; }
}

/// <summary>
/// RSA signing keys from PEM (Key Vault secret <c>token-signing-key</c>, optional <c>-previous</c>).
/// <c>kid</c> is the base64url SHA-256 thumbprint of the public key (FR-KEY-01).
/// </summary>
public sealed class SigningKeyProvider : ISigningKeyProvider
{
    private readonly List<RsaSecurityKey> _keys = [];
    private readonly List<PublicJwk> _jwks = [];

    public SigningKeyProvider(string? currentPem, string? previousPem, bool allowEphemeral, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(currentPem))
        {
            Add(FromPem(currentPem));
        }
        else if (allowEphemeral)
        {
            logger.LogWarning("No token signing key configured; generated an EPHEMERAL key. Tokens become invalid on restart. Never use outside local development.");
            Add(RSA.Create(2048));
        }
        else
        {
            throw new InvalidOperationException("Token signing key is not configured (Key Vault secret 'token-signing-key').");
        }

        if (!string.IsNullOrWhiteSpace(previousPem))
            Add(FromPem(previousPem));

        Current = new SigningCredentials(_keys[0], SecurityAlgorithms.RsaSha256);
    }

    public SigningCredentials Current { get; }
    public IReadOnlyList<RsaSecurityKey> ValidationKeys => _keys;
    public IReadOnlyList<PublicJwk> PublicKeys => _jwks;

    public static string ComputeKeyId(RSA rsa) =>
        Base64UrlEncoder.Encode(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));

    private static RSA FromPem(string pem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa;
    }

    private void Add(RSA rsa)
    {
        var kid = ComputeKeyId(rsa);
        _keys.Add(new RsaSecurityKey(rsa) { KeyId = kid });
        var p = rsa.ExportParameters(false);
        _jwks.Add(new PublicJwk("RSA", kid, "sig", SecurityAlgorithms.RsaSha256,
            Base64UrlEncoder.Encode(p.Modulus!), Base64UrlEncoder.Encode(p.Exponent!)));
    }
}
