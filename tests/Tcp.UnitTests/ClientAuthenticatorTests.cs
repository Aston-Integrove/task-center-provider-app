using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Tcp.Api.Auth;

namespace Tcp.UnitTests;

public class ClientAuthenticatorTests
{
    private sealed class FakeStore : IClientStore
    {
        private readonly OAuthClient _client = new() { ClientId = "tc-tech", SecretName = "x" };
        public OAuthClient? Find(string clientId) => clientId == "tc-tech" ? _client : null;
        public string? GetSecret(OAuthClient client) => "s3cret";
    }

    private static ClientAuthResult Run(string? basicUser, string? basicPass, Dictionary<string, string>? form = null)
    {
        var ctx = new DefaultHttpContext();
        if (basicUser is not null)
            ctx.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{basicUser}:{basicPass}"));
        var collection = new FormCollection((form ?? []).ToDictionary(k => k.Key, k => new StringValues(k.Value)));
        return new ClientAuthenticator(new FakeStore()).Authenticate(ctx.Request, collection);
    }

    [Fact] // T002-03
    public void Basic_authentication_succeeds() =>
        Run("tc-tech", "s3cret").Succeeded.Should().BeTrue();

    [Fact] // T002-03
    public void Post_authentication_succeeds() =>
        Run(null, null, new() { ["client_id"] = "tc-tech", ["client_secret"] = "s3cret" }).Succeeded.Should().BeTrue();

    [Fact] // T002-03
    public void Wrong_secret_is_invalid_client_401()
    {
        var r = Run("tc-tech", "nope");
        r.Succeeded.Should().BeFalse();
        (r.Error, r.Status).Should().Be(("invalid_client", 401));
    }

    [Fact] // T002-03
    public void Unknown_client_is_invalid_client_401() =>
        Run("ghost", "s3cret").Error.Should().Be("invalid_client");

    [Fact] // T002-03
    public void Missing_credentials_is_invalid_client() =>
        Run(null, null).Error.Should().Be("invalid_client");

    [Fact] // T002-03
    public void Both_methods_supplied_is_invalid_request_400()
    {
        var r = Run("tc-tech", "s3cret", new() { ["client_id"] = "tc-tech", ["client_secret"] = "s3cret" });
        (r.Error, r.Status).Should().Be(("invalid_request", 400));
    }

    [Fact] // FR-PP-01: Destination service may repeat client_id in the body
    public void Matching_client_id_in_body_is_tolerated_with_basic() =>
        Run("tc-tech", "s3cret", new() { ["client_id"] = "tc-tech" }).Succeeded.Should().BeTrue();

    [Fact]
    public void Mismatching_client_id_in_body_is_invalid_request() =>
        Run("tc-tech", "s3cret", new() { ["client_id"] = "other" }).Error.Should().Be("invalid_request");

    [Fact]
    public void Malformed_basic_header_is_invalid_client()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "Basic !!!not-base64!!!";
        var r = new ClientAuthenticator(new FakeStore()).Authenticate(ctx.Request, new FormCollection([]));
        r.Succeeded.Should().BeFalse();
    }
}
