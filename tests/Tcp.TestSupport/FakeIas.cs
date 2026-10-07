using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Tcp.TestSupport;

/// <summary>
/// An in-memory SAP IAS: OIDC discovery, JWKS and a token endpoint. The "browser" part of the authorization code
/// flow (authorize endpoint) is driven by the test, which reads the redirect the app sent and calls back with a code.
/// </summary>
public sealed class FakeIas : HttpMessageHandler
{
    public const string Authority = "https://ias.test";
    public const string ClientId = "tc-app";
    public const string ClientSecret = "ias-secret";

    private readonly TestIssuer _signer = new(Authority);
    private readonly Dictionary<string, (string Nonce, JsonObject Claims)> _codes = [];

    public List<Dictionary<string, string>> TokenRequests { get; } = [];
    public string? ForcedNonce { get; set; }

    /// <summary>Registers the login result for the code the test will hand back to the app.</summary>
    public string IssueCode(string nonce, JsonObject claims)
    {
        var code = Guid.NewGuid().ToString("N");
        _codes[code] = (nonce, claims);
        return code;
    }

    public static Dictionary<string, string?> Settings(string? userIdClaim = null) => new()
    {
        ["App:Auth"] = "IasOidc",
        ["App:Oidc:Authority"] = Authority,
        ["App:Oidc:ClientId"] = ClientId,
        ["Secrets:ias-oidc-client-secret"] = ClientSecret,
        ["App:Oidc:UserIdClaim"] = userIdClaim ?? "user_uuid",
    };

    /// <summary>
    /// The OIDC handler builds its backchannel HttpClient in the framework's own post-configure step, so the override
    /// has to run before it: insert at the front of the options pipeline instead of appending.
    /// </summary>
    public Action<IServiceCollection> Wire() => services =>
        services.Insert(0, ServiceDescriptor.Singleton<IPostConfigureOptions<OpenIdConnectOptions>>(
            new PostConfigureOptions<OpenIdConnectOptions>("AppOidc", o => o.BackchannelHttpHandler = this)));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        if (url == Authority + "/.well-known/openid-configuration")
            return Json(new JsonObject
            {
                ["issuer"] = Authority,
                ["authorization_endpoint"] = Authority + "/oauth2/authorize",
                ["token_endpoint"] = Authority + "/oauth2/token",
                ["jwks_uri"] = Authority + "/oauth2/certs",
                ["response_types_supported"] = new JsonArray("code"),
                ["subject_types_supported"] = new JsonArray("public"),
                ["id_token_signing_alg_values_supported"] = new JsonArray("RS256"),
                ["token_endpoint_auth_methods_supported"] = new JsonArray("client_secret_post", "client_secret_basic"),
                ["code_challenge_methods_supported"] = new JsonArray("S256"),
            }.ToJsonString());

        if (url == Authority + "/oauth2/certs") return Json(_signer.JwksJson());

        if (url == Authority + "/oauth2/token" && request.Method == HttpMethod.Post)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var form = body.Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.Length > 1 ? p[1] : ""));
            TokenRequests.Add(form);

            if (!form.TryGetValue("code", out var code) || !_codes.Remove(code, out var login))
                return Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);

            var claims = (JsonObject)login.Claims.DeepClone();
            claims["nonce"] = ForcedNonce ?? login.Nonce;
            var idToken = _signer.Mint(claims, TimeSpan.FromMinutes(5), audience: ClientId);
            return Json(new JsonObject
            {
                ["id_token"] = idToken, ["access_token"] = "opaque-access-token", ["token_type"] = "Bearer", ["expires_in"] = 3600,
            }.ToJsonString());
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
