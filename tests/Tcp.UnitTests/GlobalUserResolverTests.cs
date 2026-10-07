using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tcp.Api.Auth;
using Tcp.Domain.Identity;
using Tcp.TestSupport;

namespace Tcp.UnitTests;

public class GlobalUserResolverTests
{
    private const string Alice = "0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f";
    private const string Bob = "11111111-2222-3333-4444-555555555555";
    private const string Inactive = "99999999-8888-7777-6666-555555555555";

    private static GlobalUserResolver Create(OAuthOptions? options = null)
    {
        var directory = new InMemoryUserDirectory()
            .Add(new DirectoryUser(Alice, "alice", "alice@example.com", "Alice", true))
            .Add(new DirectoryUser(Bob, "bob", "shared@example.com", "Bob", true))
            .Add(new DirectoryUser("22222222-2222-2222-2222-222222222222", "bob2", "shared@example.com", "Bob Two", true))
            .Add(new DirectoryUser(Inactive, "gone", "gone@example.com", "Gone", false));
        return new GlobalUserResolver(directory, Options.Create(options ?? new OAuthOptions()), NullLogger<GlobalUserResolver>.Instance);
    }

    private static JsonObject Claims(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Theory] // T002-09
    [InlineData("""{"user_uuid":"0B3C7D1E-4F5A-4B6C-8D9E-0A1B2C3D4E5F"}""", Alice, "user_uuid")] // normalised to lower-case
    [InlineData("""{"ext_attr":{"user_uuid":"0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f"}}""", Alice, "ext_attr.user_uuid")]
    [InlineData("""{"sub":"11111111-2222-3333-4444-555555555555"}""", Bob, "sub")]
    [InlineData("""{"user_uuid":"11111111-2222-3333-4444-555555555555","sub":"0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f"}""", Bob, "user_uuid")] // order wins
    [InlineData("""{"user_uuid":"not-a-guid","sub":"0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f"}""", Alice, "sub")] // skips non-GUID
    [InlineData("""{"user_uuid":"ffffffff-ffff-ffff-ffff-ffffffffffff","email":"ALICE@example.com"}""", Alice, "email")] // unknown id -> e-mail fallback
    public async Task Resolves_by_claim_order(string claims, string expectedId, string expectedVia)
    {
        var result = await Create().ResolveAsync(Claims(claims), default);

        result.Should().NotBeNull();
        result!.User.GlobalUserId.Should().Be(expectedId);
        result.Via.Should().Be(expectedVia);
    }

    [Theory] // T002-09
    [InlineData("""{"sub":"auth0|abc123"}""")] // sub that is not a GUID
    [InlineData("""{"sub":"ffffffff-ffff-ffff-ffff-ffffffffffff"}""")] // GUID not in the store
    [InlineData("""{"email":"shared@example.com"}""")] // ambiguous e-mail
    [InlineData("""{"email":"nobody@example.com"}""")]
    [InlineData("""{"user_uuid":"99999999-8888-7777-6666-555555555555"}""")] // inactive
    [InlineData("""{"email":"gone@example.com"}""")] // inactive via e-mail
    [InlineData("{}")]
    public async Task Returns_null_when_unresolvable(string claims) =>
        (await Create().ResolveAsync(Claims(claims), default)).Should().BeNull();

    [Fact]
    public async Task Custom_claim_order_is_honoured()
    {
        var options = new OAuthOptions { UserIdClaims = ["employee_guid"] };
        var result = await Create(options).ResolveAsync(
            Claims("""{"employee_guid":"0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f","user_uuid":"11111111-2222-3333-4444-555555555555"}"""), default);
        result!.User.GlobalUserId.Should().Be(Alice);
    }

    [Fact]
    public async Task Claim_names_containing_dots_are_matched_exactly()
    {
        var result = await Create().ResolveAsync(
            Claims("""{"ext_attr.user_uuid":"0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f"}"""), default);
        result!.User.GlobalUserId.Should().Be(Alice);
    }
}
