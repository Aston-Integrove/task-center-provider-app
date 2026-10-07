using System.Net;
using Tcp.Api.Auth;
using Tcp.TestSupport;
using static Tcp.TestSupport.OAuthTestClient;

namespace Tcp.ContractTests;

public class OAuthContractTests : IDisposable
{
    private static readonly OpenApiContract Contract =
        OpenApiContract.Load("specs/002-auth-token-service/contracts/oauth.openapi.yaml");

    private readonly TcpFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static async Task<(HttpStatusCode Status, string Body)> Read(HttpResponseMessage r) =>
        (r.StatusCode, await r.Content.ReadAsStringAsync());

    private static Dictionary<string, string> ClientCredentials() => new() { ["grant_type"] = GrantTypes.ClientCredentials };

    [Fact] // T002-02, T002-04
    public async Task Token_success_matches_contract()
    {
        var (status, body) = await Read(await PostTokenAsync(_factory.CreateClient(), ClientCredentials(), "tc-tech", TcpFactory.TechSecret));

        status.Should().Be(HttpStatusCode.OK);
        OpenApiContract.AssertValid(Contract.ValidateResponse("/oauth/token", "post", 200, body), "token 200");
    }

    [Fact]
    public async Task Token_oauth_error_matches_contract()
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "password" };
        var (status, body) = await Read(await PostTokenAsync(_factory.CreateClient(), form, "tc-tech", TcpFactory.TechSecret));

        status.Should().Be(HttpStatusCode.BadRequest);
        OpenApiContract.AssertValid(Contract.ValidateResponse("/oauth/token", "post", 400, body), "token 400");
    }

    [Fact]
    public async Task Token_invalid_client_matches_contract()
    {
        var (status, body) = await Read(await PostTokenAsync(_factory.CreateClient(), ClientCredentials(), "tc-tech", "wrong"));

        status.Should().Be(HttpStatusCode.Unauthorized);
        OpenApiContract.AssertValid(Contract.ValidateResponse("/oauth/token", "post", 401, body), "token 401");
    }

    [Fact]
    public async Task Jwks_matches_contract()
    {
        var (status, body) = await Read(await _factory.CreateClient().GetAsync("/.well-known/jwks.json"));

        status.Should().Be(HttpStatusCode.OK);
        OpenApiContract.AssertValid(Contract.ValidateResponse("/.well-known/jwks.json", "get", 200, body), "jwks");
    }

    [Fact]
    public async Task Discovery_matches_contract()
    {
        var (status, body) = await Read(await _factory.CreateClient().GetAsync("/.well-known/openid-configuration"));

        status.Should().Be(HttpStatusCode.OK);
        OpenApiContract.AssertValid(Contract.ValidateResponse("/.well-known/openid-configuration", "get", 200, body), "discovery");
    }
}

public class OpenApiContractValidatorSelfTests
{
    private static readonly OpenApiContract Contract =
        OpenApiContract.Load("specs/002-auth-token-service/contracts/oauth.openapi.yaml");

    [Fact] // the harness must be able to fail, otherwise contract tests prove nothing
    public void Validator_reports_missing_required_properties()
    {
        Contract.ValidateResponse("/oauth/token", "post", 200, """{"token_type":"Bearer","expires_in":900}""").Should().NotBeEmpty();
    }

    [Fact]
    public void Validator_reports_enum_and_range_violations()
    {
        Contract.ValidateResponse("/oauth/token", "post", 200,
            """{"access_token":"x","token_type":"MAC","expires_in":901}""").Should().NotBeEmpty();
        Contract.ValidateResponse("/oauth/token", "post", 400, """{"error":"made_up"}""").Should().NotBeEmpty();
    }

    [Fact]
    public void Validator_accepts_conforming_bodies()
    {
        Contract.ValidateResponse("/oauth/token", "post", 200,
            """{"access_token":"x","token_type":"Bearer","expires_in":900,"scope":"spi.tech"}""").Should().BeEmpty();
    }

    [Fact]
    public void Validator_flags_undeclared_status_codes() =>
        Contract.ValidateResponse("/oauth/token", "post", 418, "{}").Should().NotBeEmpty();
}
