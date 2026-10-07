using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Tcp.Api.Auth;
using Tcp.TestSupport;
using static Tcp.TestSupport.OAuthTestClient;

namespace Tcp.IntegrationTests;

public class TokenEndpointTests(AuthFixture fx) : IClassFixture<AuthFixture>
{
    private static Dictionary<string, string> ClientCredentials(string? scope = null)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = GrantTypes.ClientCredentials };
        if (scope is not null) form["scope"] = scope;
        return form;
    }

    [Fact] // T002-05, US-002-1.1
    public async Task Client_credentials_with_basic_auth_issues_a_signed_technical_token()
    {
        var client = fx.CreateClient();

        var response = await PostTokenAsync(client, ClientCredentials(), "tc-tech", TcpFactory.TechSecret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        body["token_type"]!.GetValue<string>().Should().Be("Bearer");
        body["expires_in"]!.GetValue<int>().Should().Be(900);
        body["scope"]!.GetValue<string>().Should().Be("spi.tech");

        var jwt = new JsonWebToken(body["access_token"]!.GetValue<string>());
        jwt.Alg.Should().Be("RS256");
        jwt.Issuer.Should().Be(TcpFactory.PublicBaseUrl);
        jwt.Audiences.Should().Equal("tc-provider");
        jwt.GetPayloadValue<string>("client_id").Should().Be("tc-tech");
        jwt.GetPayloadValue<string>("scope").Should().Be("spi.tech");
        jwt.Subject.Should().Be("tc-tech");
        (jwt.ValidTo - jwt.ValidFrom).TotalSeconds.Should().BeApproximately(900, 1);
        jwt.Id.Should().NotBeNullOrEmpty();

        var jwks = await client.GetFromJsonAsync<JsonObject>("/.well-known/jwks.json");
        jwks!["keys"]!.AsArray().Select(k => k!["kid"]!.GetValue<string>()).Should().Contain(jwt.Kid);
    }

    [Fact] // T002-03
    public async Task Client_credentials_with_post_auth_works()
    {
        var form = ClientCredentials();
        form["client_id"] = "tc-tech";
        form["client_secret"] = TcpFactory.TechSecret;

        var response = await PostTokenAsync(fx.CreateClient(), form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact] // US-002-1.2
    public async Task Wrong_secret_returns_401_invalid_client_with_www_authenticate()
    {
        var response = await PostTokenAsync(fx.CreateClient(), ClientCredentials(), "tc-tech", "wrong");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().StartWith("Basic");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>().Should().Be("invalid_client");
    }

    [Fact] // T002-03
    public async Task Both_authentication_methods_at_once_is_invalid_request()
    {
        var form = ClientCredentials();
        form["client_secret"] = TcpFactory.TechSecret;

        var response = await PostTokenAsync(fx.CreateClient(), form, "tc-tech", TcpFactory.TechSecret);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>().Should().Be("invalid_request");
    }

    [Fact] // US-002-1.3
    public async Task Client_using_a_grant_it_is_not_allowed_gets_unauthorized_client()
    {
        var form = new Dictionary<string, string> { ["grant_type"] = GrantTypes.JwtBearer, ["assertion"] = "x.y.z" };

        var response = await PostTokenAsync(fx.CreateClient(), form, "tc-tech", TcpFactory.TechSecret);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>().Should().Be("unauthorized_client");
    }

    [Fact] // US-002-1.4
    public async Task Scope_outside_the_clients_allowed_scopes_is_invalid_scope()
    {
        var response = await PostTokenAsync(fx.CreateClient(), ClientCredentials("spi.user"), "tc-tech", TcpFactory.TechSecret);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>().Should().Be("invalid_scope");
    }

    [Fact] // US-002-1.4
    public async Task Explicit_allowed_scope_is_accepted()
    {
        var response = await PostTokenAsync(fx.CreateClient(), ClientCredentials("spi.tech"), "tc-tech", TcpFactory.TechSecret);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact] // T002-04
    public async Task Unsupported_grant_type_is_rejected()
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "password", ["username"] = "a", ["password"] = "b" };

        var response = await PostTokenAsync(fx.CreateClient(), form, "tc-tech", TcpFactory.TechSecret);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>().Should().Be("unsupported_grant_type");
    }

    [Fact] // T002-04
    public async Task Missing_grant_type_is_invalid_request()
    {
        var response = await PostTokenAsync(fx.CreateClient(), new Dictionary<string, string>(), "tc-tech", TcpFactory.TechSecret);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>().Should().Be("invalid_request");
    }

    [Fact] // T002-04, FR-TOK-01
    public async Task Non_form_content_type_is_invalid_request()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token")
        {
            Content = new StringContent("""{"grant_type":"client_credentials"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = Basic("tc-tech", TcpFactory.TechSecret);

        var response = await fx.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>().Should().Be("invalid_request");
    }

    [Fact] // plan 002 security notes: form max 32 KB
    public async Task Oversized_body_is_rejected()
    {
        var form = ClientCredentials();
        form["padding"] = new string('a', 40 * 1024);

        var response = await PostTokenAsync(fx.CreateClient(), form, "tc-tech", TcpFactory.TechSecret);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact] // US-002-3
    public async Task Scim_client_receives_a_scim_scoped_token_for_its_own_audience()
    {
        var response = await PostTokenAsync(fx.CreateClient(), ClientCredentials(), "ips-scim", TcpFactory.ScimSecret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        body["scope"]!.GetValue<string>().Should().Be("scim");
        body["expires_in"]!.GetValue<int>().Should().Be(900);
        new JsonWebToken(body["access_token"]!.GetValue<string>()).Audiences.Should().Equal("tc-provider-scim");
    }

    [Fact] // T002-07, FR-TOK-06
    public async Task Sixty_first_request_within_a_minute_is_rate_limited()
    {
        using var factory = new TcpFactory(settings: new() { ["OAuth:TokenRateLimitPerMinute"] = "60" });
        var client = factory.CreateClient();

        for (var i = 0; i < 60; i++)
        {
            var ok = await PostTokenAsync(client, ClientCredentials(), "tc-tech", TcpFactory.TechSecret);
            ok.StatusCode.Should().Be(HttpStatusCode.OK, $"request {i + 1}");
        }

        var limited = await PostTokenAsync(client, ClientCredentials(), "tc-tech", TcpFactory.TechSecret);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.Should().NotBeNull();

        // Another client is unaffected.
        var other = await PostTokenAsync(client, ClientCredentials(), "ips-scim", TcpFactory.ScimSecret);
        other.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact] // NFR-002-02
    public async Task Failed_authentication_never_logs_the_secret()
    {
        var form = ClientCredentials();
        form["client_secret"] = "SUPER-SECRET-VALUE";
        form["client_id"] = "tc-tech";

        await PostTokenAsync(fx.CreateClient(), form);

        fx.Logs.Messages.Should().NotContain(m => m.Contains("SUPER-SECRET-VALUE"));
    }

    [Fact] // US-002-5
    public async Task Discovery_documents_are_anonymous_and_consistent()
    {
        var client = fx.CreateClient();

        var config = (await client.GetFromJsonAsync<JsonObject>("/.well-known/openid-configuration"))!;
        config["issuer"]!.GetValue<string>().Should().Be(TcpFactory.PublicBaseUrl);
        config["token_endpoint"]!.GetValue<string>().Should().Be(TcpFactory.PublicBaseUrl + "/oauth/token");
        config["jwks_uri"]!.GetValue<string>().Should().Be(TcpFactory.PublicBaseUrl + "/.well-known/jwks.json");
        config["grant_types_supported"]!.AsArray().Select(g => g!.GetValue<string>())
            .Should().BeEquivalentTo(GrantTypes.ClientCredentials, GrantTypes.JwtBearer);
        config["token_endpoint_auth_methods_supported"]!.AsArray().Select(g => g!.GetValue<string>())
            .Should().BeEquivalentTo("client_secret_basic", "client_secret_post");

        var jwks = (await client.GetFromJsonAsync<JsonObject>("/.well-known/jwks.json"))!;
        var key = jwks["keys"]!.AsArray().Single()!.AsObject();
        key["kty"]!.GetValue<string>().Should().Be("RSA");
        key["alg"]!.GetValue<string>().Should().Be("RS256");
        key.ContainsKey("d").Should().BeFalse();
    }
}
