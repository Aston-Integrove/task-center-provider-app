using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Tcp.TestSupport;

/// <summary>
/// A fake trusted identity provider: owns an RSA key, mints assertions with arbitrary claims, and serves its
/// JWKS through <see cref="FakeIssuerHandler"/> so no network is involved.
/// </summary>
public sealed class TestIssuer
{
    public const string OidcIssuer = "https://issuer.test";
    public const string XsuaaIssuer = "https://xsuaa.test/oauth/token";

    private readonly JsonWebTokenHandler _handler = new();
    private readonly List<RsaSecurityKey> _published = [];
    private RsaSecurityKey _signing;

    public TestIssuer(string issuer = OidcIssuer)
    {
        Issuer = issuer;
        _signing = NewKey();
        _published.Add(_signing);
    }

    public string Issuer { get; }
    public bool IsXsuaa => Issuer.EndsWith("/oauth/token", StringComparison.Ordinal);
    public int JwksRequests { get; private set; }
    public int DiscoveryRequests { get; private set; }
    public string CurrentKeyId => _signing.KeyId;

    /// <summary>Base URL where discovery/JWKS are served.</summary>
    public string BaseUrl => IsXsuaa ? Issuer[..^"/oauth/token".Length] : Issuer.TrimEnd('/');

    public string DiscoveryUrl => BaseUrl + "/.well-known/openid-configuration";
    public string JwksUrl => IsXsuaa ? BaseUrl + "/token_keys" : BaseUrl + "/jwks";

    /// <summary>Starts signing with a new key. The old key stops being published unless <paramref name="keepOld"/>.</summary>
    public void Rotate(bool keepOld = false)
    {
        _signing = NewKey();
        if (!keepOld) _published.Clear();
        _published.Add(_signing);
    }

    public string Mint(
        JsonObject? claims = null,
        TimeSpan? lifetime = null,
        DateTimeOffset? notBefore = null,
        string? issuer = null,
        string? audience = null,
        SigningCredentials? credentials = null)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = claims is null ? new JsonObject() : (JsonObject)claims.DeepClone();
        payload["iss"] ??= issuer ?? Issuer;
        payload["iat"] ??= now.ToUnixTimeSeconds();
        payload["exp"] ??= (now + (lifetime ?? TimeSpan.FromMinutes(5))).ToUnixTimeSeconds();
        if (notBefore is not null) payload["nbf"] = notBefore.Value.ToUnixTimeSeconds();
        if (audience is not null) payload["aud"] = audience;

        return _handler.CreateToken(payload.ToJsonString(), credentials ?? new SigningCredentials(_signing, SecurityAlgorithms.RsaSha256));
    }

    /// <summary>A token signed by a key this issuer never publishes.</summary>
    public string MintWithForeignKey(JsonObject? claims = null) =>
        Mint(claims, credentials: new SigningCredentials(NewKey(), SecurityAlgorithms.RsaSha256));

    public string JwksJson()
    {
        var keys = new JsonArray();
        foreach (var k in _published)
        {
            var p = k.Rsa.ExportParameters(false);
            keys.Add(new JsonObject
            {
                ["kty"] = "RSA", ["kid"] = k.KeyId, ["use"] = "sig", ["alg"] = "RS256",
                ["n"] = Base64UrlEncoder.Encode(p.Modulus!), ["e"] = Base64UrlEncoder.Encode(p.Exponent!),
            });
        }
        return new JsonObject { ["keys"] = keys }.ToJsonString();
    }

    internal HttpResponseMessage? Handle(Uri uri)
    {
        var url = uri.ToString();
        if (!IsXsuaa && string.Equals(url, DiscoveryUrl, StringComparison.Ordinal))
        {
            DiscoveryRequests++;
            return Json(new JsonObject { ["issuer"] = Issuer, ["jwks_uri"] = JwksUrl }.ToJsonString());
        }
        if (string.Equals(url, JwksUrl, StringComparison.Ordinal))
        {
            JwksRequests++;
            return Json(JwksJson());
        }
        return null;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static RsaSecurityKey NewKey()
    {
        var rsa = RSA.Create(2048);
        return new RsaSecurityKey(rsa) { KeyId = Guid.NewGuid().ToString("N") };
    }
}

/// <summary>Routes outbound JWKS/discovery requests to the in-memory issuers; anything else is a 404.</summary>
public sealed class FakeIssuerHandler(IReadOnlyList<TestIssuer> issuers) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        foreach (var issuer in issuers)
        {
            var response = issuer.Handle(request.RequestUri!);
            if (response is not null) return Task.FromResult(response);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
