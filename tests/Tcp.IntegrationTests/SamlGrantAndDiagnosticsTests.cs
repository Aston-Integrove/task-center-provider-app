using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Xml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Tcp.Api.Auth;
using Tcp.Domain.Identity;
using Tcp.TestSupport;
using static Tcp.TestSupport.OAuthTestClient;

namespace Tcp.IntegrationTests;

public sealed class SamlGrantTests : IDisposable
{
    private readonly SamlTestIssuer _idp = new();
    private readonly TcpFactory _factory;

    public SamlGrantTests()
    {
        var directory = new InMemoryUserDirectory()
            .Add(new DirectoryUser(AuthFixture.AliceId, "alice", "alice@example.com", "Alice Example", true));
        _factory = new TcpFactory(
            settings: new()
            {
                ["Saml:Enabled"] = "true",
                ["Saml:TrustCertificate"] = _idp.CertificatePem,
                // allow the PP client to use the SAML grant too
                ["OAuth:Clients:1:AllowedGrants:1"] = GrantTypes.Saml2Bearer,
            },
            configureServices: s => s.AddSingleton<IUserDirectory>(directory));
    }

    private Task<HttpResponseMessage> ExchangeAsync(string assertion) =>
        PostTokenAsync(_factory.CreateClient(),
            new() { ["grant_type"] = GrantTypes.Saml2Bearer, ["assertion"] = assertion }, "tc-pp", TcpFactory.PpSecret);

    private static async Task<string?> DescriptionAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        body["error"]!.GetValue<string>().Should().Be("invalid_grant");
        return body["error_description"]?.GetValue<string>();
    }

    [Fact] // T002-14
    public async Task Valid_signed_assertion_yields_user_token()
    {
        var response = await ExchangeAsync(_idp.Build(new() { NameId = AuthFixture.AliceId }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        body["scope"]!.GetValue<string>().Should().Be("spi.user");
        new JsonWebToken(body["access_token"]!.GetValue<string>()).Subject.Should().Be(AuthFixture.AliceId);
    }

    [Fact]
    public async Task Global_user_id_can_come_from_a_configured_attribute()
    {
        using var factory = new TcpFactory(
            settings: new()
            {
                ["Saml:Enabled"] = "true", ["Saml:TrustCertificate"] = _idp.CertificatePem,
                ["Saml:UserIdAttribute"] = "user_uuid", ["OAuth:Clients:1:AllowedGrants:1"] = GrantTypes.Saml2Bearer,
            },
            configureServices: s => s.AddSingleton<IUserDirectory>(new InMemoryUserDirectory()
                .Add(new DirectoryUser(AuthFixture.AliceId, "alice", null, null, true))));
        var assertion = _idp.Build(new() { NameId = "alice@corp.example", AttributeName = "user_uuid", AttributeValue = AuthFixture.AliceId });

        var response = await PostTokenAsync(factory.CreateClient(),
            new() { ["grant_type"] = GrantTypes.Saml2Bearer, ["assertion"] = assertion }, "tc-pp", TcpFactory.PpSecret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Tampered_assertion_fails_signature_validation()
    {
        var assertion = _idp.Build(new() { NameId = AuthFixture.AliceId }, doc =>
        {
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("saml", SamlTestIssuer.Ns);
            doc.SelectSingleNode("//saml:NameID", ns)!.InnerText = AuthFixture.InactiveId;
        });

        (await DescriptionAsync(await ExchangeAsync(assertion))).Should().Be("signature invalid");
    }

    [Fact]
    public async Task Unsigned_assertion_is_rejected() =>
        (await DescriptionAsync(await ExchangeAsync(_idp.Build(new() { NameId = AuthFixture.AliceId, Sign = false }))))
            .Should().Be("assertion must carry exactly one enveloped signature");

    [Fact]
    public async Task Assertion_signed_with_another_key_is_rejected()
    {
        using var other = RSA.Create(2048);
        var assertion = _idp.Build(new() { NameId = AuthFixture.AliceId, SigningKey = other });

        (await DescriptionAsync(await ExchangeAsync(assertion))).Should().Be("signature invalid");
    }

    [Fact]
    public async Task Sha1_signatures_are_rejected()
    {
        var assertion = _idp.Build(new()
        {
            NameId = AuthFixture.AliceId,
            SignatureMethod = "http://www.w3.org/2000/09/xmldsig#rsa-sha1",
            DigestMethod = "http://www.w3.org/2000/09/xmldsig#sha1",
        });

        (await DescriptionAsync(await ExchangeAsync(assertion))).Should().Be("unsupported signature algorithm");
    }

    [Fact]
    public async Task Expired_assertion_is_rejected() =>
        (await DescriptionAsync(await ExchangeAsync(_idp.Build(new()
        {
            NameId = AuthFixture.AliceId,
            NotBefore = DateTimeOffset.UtcNow.AddHours(-2),
            NotOnOrAfter = DateTimeOffset.UtcNow.AddHours(-1),
        })))).Should().Be("assertion expired");

    [Fact]
    public async Task Wrong_audience_is_rejected() =>
        (await DescriptionAsync(await ExchangeAsync(_idp.Build(new() { NameId = AuthFixture.AliceId, Audience = "https://other.test" }))))
            .Should().Be("audience mismatch");

    [Fact]
    public async Task Unknown_user_is_rejected() =>
        (await DescriptionAsync(await ExchangeAsync(_idp.Build(new() { NameId = AuthFixture.UnknownId }))))
            .Should().Be("unknown user");

    [Fact] // signature wrapping: a second, unsigned assertion must not be accepted
    public async Task Injected_second_assertion_is_rejected()
    {
        var assertion = _idp.Build(new() { NameId = AuthFixture.AliceId }, doc =>
        {
            var wrapper = doc.CreateElement("saml", "Extensions", SamlTestIssuer.Ns);
            wrapper.AppendChild(doc.DocumentElement!.CloneNode(true));
            doc.DocumentElement!.AppendChild(wrapper);
        });

        (await DescriptionAsync(await ExchangeAsync(assertion))).Should().Be("expected exactly one Assertion");
    }

    [Fact]
    public async Task Doctype_xxe_payload_is_rejected_without_resolving_entities()
    {
        var xml = """<?xml version="1.0"?><!DOCTYPE a [<!ENTITY x SYSTEM "file:///etc/passwd">]><a>&x;</a>""";
        var encoded = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(System.Text.Encoding.UTF8.GetBytes(xml));

        (await DescriptionAsync(await ExchangeAsync(encoded))).Should().Be("assertion is not valid base64url SAML XML");
    }

    [Fact]
    public async Task Grant_is_not_offered_when_saml_is_disabled()
    {
        using var disabled = new TcpFactory();
        var response = await PostTokenAsync(disabled.CreateClient(),
            new() { ["grant_type"] = GrantTypes.Saml2Bearer, ["assertion"] = "x" }, "tc-pp", TcpFactory.PpSecret);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>().Should().Be("unsupported_grant_type");
    }

    public void Dispose()
    {
        _factory.Dispose();
        _idp.Dispose();
    }
}

public class AdminDiagnosticsTests(AuthFixture fx) : IClassFixture<AuthFixture>
{
    private static HttpRequestMessage Decode(string assertion, bool admin = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/api/diagnostics/decode-assertion")
        {
            Content = JsonContent.Create(new { assertion }),
        };
        if (admin) request.Headers.Authorization = Basic(TcpFactory.AdminUser, TcpFactory.AdminPassword);
        return request;
    }

    [Fact] // T002-13
    public async Task Admin_can_decode_an_assertion_and_see_how_the_user_would_resolve()
    {
        var token = fx.Oidc.Mint(AuthFixture.AliceClaims());

        var response = await fx.CreateClient().SendAsync(Decode(token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(token.Split('.')[2]);
        var body = JsonNode.Parse(raw)!.AsObject();
        body["header"]!["alg"]!.GetValue<string>().Should().Be("RS256");
        body["claimNames"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain(["user_uuid", "email", "iss"]);
        body["trustedIssuer"]!.GetValue<bool>().Should().BeTrue();
        body["userResolution"]!["globalUserId"]!.GetValue<string>().Should().Be(AuthFixture.AliceId);
        body["userResolution"]!["via"]!.GetValue<string>().Should().Be("user_uuid");
    }

    [Fact] // T002-12, T002-13
    public async Task Decode_endpoint_requires_admin_credentials()
    {
        var token = fx.Oidc.Mint(AuthFixture.AliceClaims());
        var client = fx.CreateClient();

        (await client.SendAsync(Decode(token, admin: false))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var bearerOnly = Decode(token, admin: false);
        bearerOnly.Headers.Authorization = new("Bearer", await GetTechTokenAsync(client));
        (await client.SendAsync(bearerOnly)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Decode_flags_untrusted_issuers_and_rejects_non_jwts()
    {
        var client = fx.CreateClient();

        var rogue = new TestIssuer("https://rogue.test").Mint(new JsonObject { ["sub"] = "x" });
        var body = await (await client.SendAsync(Decode(rogue))).Content.ReadFromJsonAsync<JsonObject>();
        body!["trustedIssuer"]!.GetValue<bool>().Should().BeFalse();
        body["userResolution"]!["resolved"]!.GetValue<bool>().Should().BeFalse();

        (await client.SendAsync(Decode("not-a-jwt"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
