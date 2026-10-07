using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Tcp.Api.Auth;
using Tcp.TestSupport;
using static Tcp.TestSupport.OAuthTestClient;

namespace Tcp.IntegrationTests;

public class JwtBearerGrantTests(AuthFixture fx) : IClassFixture<AuthFixture>
{
    private static Dictionary<string, string> Grant(string assertion, Dictionary<string, string>? extra = null)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = GrantTypes.JwtBearer, ["assertion"] = assertion };
        foreach (var (k, v) in extra ?? []) form[k] = v;
        return form;
    }

    private static Task<HttpResponseMessage> Exchange(HttpClient client, string assertion, Dictionary<string, string>? extra = null) =>
        PostTokenAsync(client, Grant(assertion, extra), "tc-pp", TcpFactory.PpSecret);

    private static async Task<(string Error, string? Description)> ErrorOf(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        return (body["error"]!.GetValue<string>(), body["error_description"]?.GetValue<string>());
    }

    [Fact] // T002-10, US-002-2.1
    public async Task Valid_assertion_yields_a_user_token_with_global_user_id_as_subject()
    {
        var response = await Exchange(fx.CreateClient(), fx.Oidc.Mint(AuthFixture.AliceClaims()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        body["scope"]!.GetValue<string>().Should().Be("spi.user");
        body["expires_in"]!.GetValue<int>().Should().BeInRange(1, 600);

        var jwt = new JsonWebToken(body["access_token"]!.GetValue<string>());
        jwt.Subject.Should().Be(AuthFixture.AliceId);
        jwt.GetPayloadValue<string>("client_id").Should().Be("tc-pp");
        jwt.GetPayloadValue<string>("email").Should().Be("alice@example.com");
        jwt.GetPayloadValue<string>("name").Should().Be("Alice Example");
        jwt.GetPayloadValue<string>("orig_iss").Should().Be(TestIssuer.OidcIssuer);
    }

    [Fact] // FR-PP-05
    public async Task Token_lifetime_never_exceeds_the_assertions_remaining_validity()
    {
        var assertion = fx.Oidc.Mint(AuthFixture.AliceClaims(), lifetime: TimeSpan.FromSeconds(120));

        var body = (await (await Exchange(fx.CreateClient(), assertion)).Content.ReadFromJsonAsync<JsonObject>())!;

        body["expires_in"]!.GetValue<int>().Should().BeInRange(100, 120);
    }

    [Fact] // US-002-2.2
    public async Task Untrusted_issuer_is_invalid_grant()
    {
        var rogue = new TestIssuer("https://rogue.test");

        var (error, description) = await ErrorOf(await Exchange(fx.CreateClient(), rogue.Mint(AuthFixture.AliceClaims())));

        (error, description).Should().Be(("invalid_grant", "untrusted issuer"));
    }

    [Fact] // US-002-2.3
    public async Task Expired_assertion_is_invalid_grant()
    {
        var assertion = fx.Oidc.Mint(AuthFixture.AliceClaims(), lifetime: TimeSpan.FromMinutes(-10));

        (await ErrorOf(await Exchange(fx.CreateClient(), assertion))).Error.Should().Be("invalid_grant");
    }

    [Fact]
    public async Task Assertion_not_yet_valid_is_invalid_grant()
    {
        var assertion = fx.Oidc.Mint(AuthFixture.AliceClaims(), notBefore: DateTimeOffset.UtcNow.AddMinutes(10));

        (await ErrorOf(await Exchange(fx.CreateClient(), assertion))).Error.Should().Be("invalid_grant");
    }

    [Fact] // US-002-2.4
    public async Task Unknown_user_is_invalid_grant_and_logs_claim_names_only()
    {
        var claims = new JsonObject { ["user_uuid"] = AuthFixture.UnknownId, ["secret_looking_value"] = "do-not-log-this" };
        var assertion = fx.Oidc.Mint(claims);

        var (error, description) = await ErrorOf(await Exchange(fx.CreateClient(), assertion));

        (error, description).Should().Be(("invalid_grant", "unknown user"));
        fx.Logs.Messages.Should().Contain(m => m.Contains("claim names present") && m.Contains("user_uuid"));
        fx.Logs.Messages.Should().NotContain(m => m.Contains("do-not-log-this") || m.Contains(assertion));
    }

    [Fact] // US-002-2.4
    public async Task Inactive_user_is_invalid_grant()
    {
        var assertion = fx.Oidc.Mint(new JsonObject { ["user_uuid"] = AuthFixture.InactiveId });

        (await ErrorOf(await Exchange(fx.CreateClient(), assertion))).Description.Should().Be("unknown user");
    }

    [Fact] // US-002-2.5, T002-13
    public async Task Assertion_claim_logging_records_names_and_resolved_user_but_never_the_token()
    {
        var assertion = fx.Oidc.Mint(AuthFixture.AliceClaims());

        await Exchange(fx.CreateClient(), assertion);

        fx.Logs.Messages.Should().Contain(m =>
            m.StartsWith("Assertion claims from") && m.Contains("user_uuid") && m.Contains(AuthFixture.AliceId));
        fx.Logs.Messages.Should().NotContain(m => m.Contains(assertion));
    }

    [Fact]
    public async Task Assertion_signed_by_a_foreign_key_is_rejected()
    {
        var assertion = fx.Oidc.MintWithForeignKey(AuthFixture.AliceClaims());

        (await ErrorOf(await Exchange(fx.CreateClient(), assertion))).Error.Should().Be("invalid_grant");
    }

    [Fact]
    public async Task Tampered_payload_is_rejected()
    {
        var parts = fx.Oidc.Mint(AuthFixture.AliceClaims()).Split('.');
        var forged = new JsonObject { ["iss"] = TestIssuer.OidcIssuer, ["user_uuid"] = AuthFixture.AliceId, ["exp"] = DateTimeOffset.UtcNow.AddYears(10).ToUnixTimeSeconds() };
        var tampered = $"{parts[0]}.{Base64UrlEncoder.Encode(forged.ToJsonString())}.{parts[2]}";

        (await ErrorOf(await Exchange(fx.CreateClient(), tampered))).Error.Should().Be("invalid_grant");
    }

    [Fact] // plan 002 security notes: never accept alg=none
    public async Task Unsigned_alg_none_token_is_rejected()
    {
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode(new JsonObject
        {
            ["iss"] = TestIssuer.OidcIssuer, ["user_uuid"] = AuthFixture.AliceId, ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        }.ToJsonString());

        (await ErrorOf(await Exchange(fx.CreateClient(), $"{header}.{payload}."))).Error.Should().Be("invalid_grant");
    }

    [Fact] // plan 002 security notes: never accept HS*
    public async Task Symmetric_algorithm_token_is_rejected()
    {
        var key = new SymmetricSecurityKey(new byte[64]) { KeyId = "hs" };
        var assertion = fx.Oidc.Mint(AuthFixture.AliceClaims(), credentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        (await ErrorOf(await Exchange(fx.CreateClient(), assertion))).Description.Should().Be("unsupported signing algorithm");
    }

    [Fact] // T002-08
    public async Task Rotated_issuer_key_is_picked_up_via_refresh_on_unknown_kid()
    {
        using var rotating = new AuthFixture();
        var client = rotating.CreateClient();
        (await Exchange(client, rotating.Oidc.Mint(AuthFixture.AliceClaims()))).StatusCode.Should().Be(HttpStatusCode.OK);
        var requestsBefore = rotating.Oidc.JwksRequests;

        rotating.Oidc.Rotate();
        var response = await Exchange(client, rotating.Oidc.Mint(AuthFixture.AliceClaims()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        rotating.Oidc.JwksRequests.Should().Be(requestsBefore + 1);
    }

    [Fact] // FR-PP-02
    public async Task Xsuaa_issuer_is_supported()
    {
        var assertion = fx.Xsuaa.Mint(AuthFixture.AliceClaims());

        var response = await Exchange(fx.CreateClient(), assertion);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        new JsonWebToken((await response.Content.ReadFromJsonAsync<JsonObject>())!["access_token"]!.GetValue<string>())
            .GetPayloadValue<string>("orig_iss").Should().Be(TestIssuer.XsuaaIssuer);
    }

    [Fact] // FR-PP-04
    public async Task Nested_user_uuid_claim_and_email_fallback_are_used()
    {
        var client = fx.CreateClient();

        var nested = fx.Oidc.Mint(new JsonObject { ["ext_attr"] = new JsonObject { ["user_uuid"] = AuthFixture.AliceId } });
        (await Exchange(client, nested)).StatusCode.Should().Be(HttpStatusCode.OK);

        var byEmail = fx.Oidc.Mint(new JsonObject { ["sub"] = "P123456", ["email"] = "alice@example.com" });
        (await Exchange(client, byEmail)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact] // FR-PP-01
    public async Task Extra_destination_service_fields_are_tolerated()
    {
        var response = await Exchange(fx.CreateClient(), fx.Oidc.Mint(AuthFixture.AliceClaims()),
            new() { ["token_format"] = "jwt", ["response_type"] = "token", ["client_id"] = "tc-pp", ["scope"] = "spi.user" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Missing_or_oversized_assertion_is_invalid_request()
    {
        var client = fx.CreateClient();

        var missing = await PostTokenAsync(client, new() { ["grant_type"] = GrantTypes.JwtBearer }, "tc-pp", TcpFactory.PpSecret);
        (await ErrorOf(missing)).Error.Should().Be("invalid_request");

        var oversized = await Exchange(client, new string('a', 17 * 1024));
        (await ErrorOf(oversized)).Error.Should().Be("invalid_request");

        var garbage = await Exchange(client, "this-is-not-a-jwt");
        (await ErrorOf(garbage)).Error.Should().Be("invalid_grant");
    }

    [Fact] // user client cannot get a technical scope
    public async Task Pp_client_cannot_request_the_technical_scope()
    {
        var response = await Exchange(fx.CreateClient(), fx.Oidc.Mint(AuthFixture.AliceClaims()), new() { ["scope"] = "spi.tech" });

        (await ErrorOf(response)).Error.Should().Be("invalid_scope");
    }
}
