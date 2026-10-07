namespace Tcp.Api.Auth;

public static class GrantTypes
{
    public const string ClientCredentials = "client_credentials";
    public const string JwtBearer = "urn:ietf:params:oauth:grant-type:jwt-bearer";
    public const string Saml2Bearer = "urn:ietf:params:oauth:grant-type:saml2-bearer";
}

public static class Scopes
{
    public const string SpiTech = "spi.tech";
    public const string SpiUser = "spi.user";
    public const string Scim = "scim";
}

public sealed class OAuthClient
{
    public string ClientId { get; set; } = "";

    /// <summary>Config key suffix of the secret: read from <c>Secrets:{SecretName}</c> (Key Vault in Azure).</summary>
    public string SecretName { get; set; } = "";

    /// <summary>Direct secret for local development only; <see cref="SecretName"/> wins when both are set.</summary>
    public string? Secret { get; set; }

    public List<string> AllowedGrants { get; set; } = [];
    public List<string> AllowedScopes { get; set; } = [];
    public List<string> DefaultScopes { get; set; } = [];
    public string Audience { get; set; } = "tc-provider";
    public int TokenLifetimeSeconds { get; set; } = 900;
}

public enum AssertionIssuerType
{
    Oidc,
    Xsuaa,
}

public sealed class TrustedAssertionIssuer
{
    public string Issuer { get; set; } = "";
    public AssertionIssuerType Type { get; set; } = AssertionIssuerType.Oidc;

    /// <summary>Explicit JWKS location; otherwise derived from the issuer type (FR-PP-02).</summary>
    public string? JwksUri { get; set; }

    public bool ValidateAudience { get; set; }
    public List<string> ValidAudiences { get; set; } = [];
}

public sealed class OAuthOptions
{
    public const string Section = "OAuth";
    public static readonly string[] DefaultUserIdClaims = ["user_uuid", "ext_attr.user_uuid", "sub"];

    /// <summary>Token issuer. Empty = <c>Provider:PublicBaseUrl</c> (post-configured).</summary>
    public string Issuer { get; set; } = "";

    public List<OAuthClient> Clients { get; set; } = [];
    public List<TrustedAssertionIssuer> TrustedAssertionIssuers { get; set; } = [];

    /// <summary>Ordered claim paths (dotted = nested); empty = <see cref="DefaultUserIdClaims"/>.</summary>
    public List<string> UserIdClaims { get; set; } = [];

    public string SigningKeySecretName { get; set; } = "token-signing-key";
    public string PreviousSigningKeySecretName { get; set; } = "token-signing-key-previous";

    /// <summary>Allow generating an ephemeral signing key when none is configured (never in Production).</summary>
    public bool AllowEphemeralSigningKey { get; set; }

    public int TokenRateLimitPerMinute { get; set; } = 60;

    public IReadOnlyList<string> EffectiveUserIdClaims => UserIdClaims.Count > 0 ? UserIdClaims : DefaultUserIdClaims;
}

public sealed class SamlOptions
{
    public const string Section = "Saml";
    public bool Enabled { get; set; }

    /// <summary>Trust certificate (PEM or base64 DER) of the BTP subaccount; or Key Vault secret <c>btp-saml-trust-cert</c>.</summary>
    public string? TrustCertificate { get; set; }

    /// <summary>Expected audience; empty = <c>Provider:PublicBaseUrl</c>.</summary>
    public string? Audience { get; set; }

    /// <summary>Attribute holding the Global User ID; empty = use the NameID.</summary>
    public string? UserIdAttribute { get; set; }
}
