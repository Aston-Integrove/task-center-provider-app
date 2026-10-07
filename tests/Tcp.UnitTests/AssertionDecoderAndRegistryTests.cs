using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tcp.Api.Auth;
using Tcp.TestSupport;

namespace Tcp.UnitTests;

public class AssertionDecoderTests
{
    [Fact] // T002-13
    public void Decodes_header_and_claims_but_never_the_signature()
    {
        var issuer = new TestIssuer();
        var token = issuer.Mint(new() { ["sub"] = "abc", ["user_uuid"] = "u-1" });

        var decoded = AssertionDecoder.TryDecode(token);

        decoded.Should().NotBeNull();
        decoded!.Header["alg"]!.GetValue<string>().Should().Be("RS256");
        decoded.Header["kid"]!.GetValue<string>().Should().Be(issuer.CurrentKeyId);
        decoded.Claims["user_uuid"]!.GetValue<string>().Should().Be("u-1");
        var signature = token.Split('.')[2];
        (decoded.Header.ToJsonString() + decoded.Claims.ToJsonString()).Should().NotContain(signature);
    }

    [Theory] // T002-13
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a.b")]
    [InlineData("not.a.jwt")]
    [InlineData("a.b.c.d")]
    public void Garbage_returns_null(string? input) => AssertionDecoder.TryDecode(input).Should().BeNull();
}

public class AssertionIssuerRegistryTests
{
    private sealed class Factory(IReadOnlyList<TestIssuer> issuers) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FakeIssuerHandler(issuers));
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static (AssertionIssuerRegistry Registry, Clock Clock) Create(TestIssuer issuer, TrustedAssertionIssuer config)
    {
        var options = Options.Create(new OAuthOptions { TrustedAssertionIssuers = [config] });
        var clock = new Clock(DateTimeOffset.UtcNow);
        return (new AssertionIssuerRegistry(options, new Factory([issuer]), clock, NullLogger<AssertionIssuerRegistry>.Instance), clock);
    }

    [Fact] // T002-08
    public async Task Oidc_discovery_resolves_jwks_and_caches_for_an_hour()
    {
        var issuer = new TestIssuer();
        var config = new TrustedAssertionIssuer { Issuer = issuer.Issuer, Type = AssertionIssuerType.Oidc };
        var (registry, clock) = Create(issuer, config);

        var first = await registry.GetKeysAsync(config, false, default);
        await registry.GetKeysAsync(config, false, default);

        first.Select(k => k.KeyId).Should().Contain(issuer.CurrentKeyId);
        issuer.JwksRequests.Should().Be(1);
        issuer.DiscoveryRequests.Should().Be(1);

        clock.Advance(AssertionIssuerRegistry.CacheDuration + TimeSpan.FromSeconds(1));
        await registry.GetKeysAsync(config, false, default);
        issuer.JwksRequests.Should().Be(2);
    }

    [Fact] // T002-08
    public async Task Xsuaa_issuer_uses_token_keys_endpoint()
    {
        var issuer = new TestIssuer(TestIssuer.XsuaaIssuer);
        var config = new TrustedAssertionIssuer { Issuer = issuer.Issuer, Type = AssertionIssuerType.Xsuaa };
        var (registry, _) = Create(issuer, config);

        var keys = await registry.GetKeysAsync(config, false, default);

        keys.Select(k => k.KeyId).Should().Contain(issuer.CurrentKeyId);
        issuer.JwksUrl.Should().Be("https://xsuaa.test/token_keys");
        issuer.DiscoveryRequests.Should().Be(0);
    }

    [Fact] // T002-08
    public async Task Forced_refresh_picks_up_rotated_keys_but_is_rate_limited()
    {
        var issuer = new TestIssuer();
        var config = new TrustedAssertionIssuer { Issuer = issuer.Issuer };
        var (registry, clock) = Create(issuer, config);
        await registry.GetKeysAsync(config, false, default);

        issuer.Rotate();
        var refreshed = await registry.GetKeysAsync(config, true, default);
        refreshed.Select(k => k.KeyId).Should().Equal(issuer.CurrentKeyId);

        // A second forced refresh inside the minimum interval must NOT hit the network again.
        issuer.Rotate();
        var requestsBefore = issuer.JwksRequests;
        var cached = await registry.GetKeysAsync(config, true, default);
        issuer.JwksRequests.Should().Be(requestsBefore);
        cached.Select(k => k.KeyId).Should().NotContain(issuer.CurrentKeyId);

        clock.Advance(AssertionIssuerRegistry.MinForcedRefreshInterval + TimeSpan.FromSeconds(1));
        (await registry.GetKeysAsync(config, true, default)).Select(k => k.KeyId).Should().Contain(issuer.CurrentKeyId);
    }

    [Fact]
    public void Find_matches_issuer_exactly()
    {
        var issuer = new TestIssuer();
        var config = new TrustedAssertionIssuer { Issuer = issuer.Issuer };
        var (registry, _) = Create(issuer, config);

        registry.Find(issuer.Issuer).Should().BeSameAs(config);
        registry.Find(issuer.Issuer + "/").Should().BeNull();
        registry.Find("https://evil.test").Should().BeNull();
    }

    [Theory]
    [InlineData("https://sub.authentication.eu10.hana.ondemand.com/oauth/token", "https://sub.authentication.eu10.hana.ondemand.com/token_keys")]
    [InlineData("https://sub.authentication.eu10.hana.ondemand.com/", "https://sub.authentication.eu10.hana.ondemand.com/token_keys")]
    public void Xsuaa_jwks_uri_derivation(string issuer, string expected) =>
        AssertionIssuerRegistry.DeriveXsuaaJwksUri(issuer).Should().Be(expected);
}
