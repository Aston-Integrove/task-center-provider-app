using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Tcp.Api.Auth;
using Tcp.TestSupport;
using static Tcp.TestSupport.OAuthTestClient;

namespace Tcp.IntegrationTests;

public class ResourceProtectionTests(AuthFixture fx) : IClassFixture<AuthFixture>
{
    private async Task<string> UserTokenAsync(HttpClient client) =>
        await GetUserTokenAsync(client, fx.Oidc.Mint(AuthFixture.AliceClaims()));

    /// <summary>Mints a token with our real signing key but arbitrary claims, to test validation rules.</summary>
    private string MintOwn(
        string? issuer = null, string audience = "tc-provider", string scope = "spi.tech", string sub = "tc-tech",
        TimeSpan? lifetime = null, SigningCredentials? credentials = null)
    {
        var keys = fx.Factory.Services.GetRequiredService<ISigningKeyProvider>();
        var now = DateTime.UtcNow;
        var expires = now + (lifetime ?? TimeSpan.FromMinutes(5));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? TcpFactory.PublicBaseUrl,
            Audience = audience,
            IssuedAt = now.AddMinutes(-30),
            NotBefore = lifetime is { Ticks: < 0 } ? expires.AddMinutes(-5) : now.AddMinutes(-1),
            Expires = expires,
            SigningCredentials = credentials ?? keys.Current,
            Claims = new Dictionary<string, object> { ["scope"] = scope, ["sub"] = sub, ["client_id"] = "tc-tech" },
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string? bearer) =>
        await fx.CreateClient().SendAsync(Get(path, bearer));

    [Fact] // T002-06
    public async Task Missing_token_is_401_with_bearer_challenge()
    {
        var response = await GetAsync("/__probe/tech", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().StartWith("Bearer");
    }

    [Theory] // T002-06, US-002-4.4
    [InlineData("expired")]
    [InlineData("wrong-audience")]
    [InlineData("wrong-issuer")]
    [InlineData("foreign-key")]
    [InlineData("hs256")]
    [InlineData("tampered")]
    [InlineData("garbage")]
    public async Task Invalid_tokens_are_401_with_invalid_token_challenge(string kind)
    {
        var token = kind switch
        {
            "expired" => MintOwn(lifetime: TimeSpan.FromMinutes(-10)),
            "wrong-audience" => MintOwn(audience: "someone-else"),
            "wrong-issuer" => MintOwn(issuer: "https://evil.test"),
            "foreign-key" => MintOwn(credentials: new SigningCredentials(new RsaSecurityKey(RSA.Create(2048)) { KeyId = "x" }, SecurityAlgorithms.RsaSha256)),
            "hs256" => MintOwn(credentials: new SigningCredentials(new SymmetricSecurityKey(new byte[64]), SecurityAlgorithms.HmacSha256)),
            "tampered" => TamperPayload(MintOwn()),
            _ => "not-a-jwt",
        };

        var response = await GetAsync("/__probe/tech", token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_token");
    }

    private static string TamperPayload(string token)
    {
        var parts = token.Split('.');
        var payload = JsonNode.Parse(Base64UrlEncoder.Decode(parts[1]))!.AsObject();
        payload["scope"] = "spi.tech spi.user scim";
        return $"{parts[0]}.{Base64UrlEncoder.Encode(payload.ToJsonString())}.{parts[2]}";
    }

    [Fact] // T002-06
    public async Task Technical_token_passes_tech_and_any_but_not_user_or_scim_policies()
    {
        var token = await GetTechTokenAsync(fx.CreateClient());

        (await GetAsync("/__probe/tech", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync("/__probe/any", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync("/__probe/user", token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync("/__probe/scim", token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact] // T002-06
    public async Task Scim_token_only_passes_the_scim_policy()
    {
        var token = await GetScimTokenAsync(fx.CreateClient());

        (await GetAsync("/__probe/scim", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync("/__probe/tech", token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync("/__probe/any", token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact] // T002-06, T002-11
    public async Task User_token_passes_user_and_any_and_exposes_the_current_user()
    {
        var token = await UserTokenAsync(fx.CreateClient());

        (await GetAsync("/__probe/any", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync("/__probe/tech", token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var who = await (await GetAsync("/__probe/user", token)).Content.ReadFromJsonAsync<JsonObject>();
        who!["globalUserId"]!.GetValue<string>().Should().Be(AuthFixture.AliceId);
        who["isTechnical"]!.GetValue<bool>().Should().BeFalse();
        who["clientId"]!.GetValue<string>().Should().Be("tc-pp");
    }

    [Fact] // T002-11
    public async Task Technical_caller_has_no_global_user_id()
    {
        var token = await GetTechTokenAsync(fx.CreateClient());

        var who = await (await GetAsync("/__probe/any", token)).Content.ReadFromJsonAsync<JsonObject>();

        who!["isTechnical"]!.GetValue<bool>().Should().BeTrue();
        who["globalUserId"].Should().BeNull();
    }

    [Fact] // T002-11, US-002-4.2
    public async Task Technical_token_on_user_endpoint_returns_sap_error_user_context_required()
    {
        var token = await GetTechTokenAsync(fx.CreateClient());

        var response = await GetAsync("/task-provider/v2/__probe/user", token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.AsObject();
        error["code"]!.GetValue<string>().Should().Be("tcp.auth.userContextRequired");
        error["message"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        error["target"].Should().BeNull();
        error["details"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Anonymous_endpoint_needs_no_token()
    {
        (await GetAsync("/__probe/open", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
