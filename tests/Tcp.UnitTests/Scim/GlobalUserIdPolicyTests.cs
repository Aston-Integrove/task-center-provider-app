using Tcp.Domain.Identity;

namespace Tcp.UnitTests.Scim;

public class GlobalUserIdPolicyTests
{
    private const string Uuid = "0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f";
    private const string Other = "11111111-2222-3333-4444-555555555555";

    [Fact] // T003-04
    public void User_uuid_wins_over_external_id() =>
        GlobalUserIdPolicy.Default.Derive(Uuid, Other, null).Should().Be(Uuid);

    [Fact] // T003-04
    public void Guid_external_id_is_the_fallback() =>
        GlobalUserIdPolicy.Default.Derive(null, Other, null).Should().Be(Other);

    [Theory] // T003-04
    [InlineData("jdoe")]
    [InlineData("12345")]
    [InlineData("not-a-guid-at-all")]
    [InlineData("")]
    public void Non_guid_external_id_is_rejected(string externalId) =>
        GlobalUserIdPolicy.Default.Derive(null, externalId, null).Should().BeNull();

    [Fact] // T003-04
    public void Nothing_to_derive_from_returns_null() =>
        GlobalUserIdPolicy.Default.Derive(null, null, null).Should().BeNull();

    [Theory] // T003-04: stored normalised lower-case hyphenated
    [InlineData("0B3C7D1E-4F5A-4B6C-8D9E-0A1B2C3D4E5F")]
    [InlineData("{0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f}")]
    [InlineData("0b3c7d1e4f5a4b6c8d9e0a1b2c3d4e5f")] // 32 hex, how IAS often renders the id
    [InlineData("  0b3c7d1e-4f5a-4b6c-8d9e-0a1b2c3d4e5f  ")]
    public void Result_is_normalised(string input) =>
        GlobalUserIdPolicy.Default.Derive(input, null, null).Should().Be(Uuid);

    [Fact]
    public void Invalid_user_uuid_falls_through_to_the_next_source() =>
        GlobalUserIdPolicy.Default.Derive("garbage", Other, null).Should().Be(Other);

    [Fact]
    public void Empty_guid_is_not_a_valid_id() =>
        GlobalUserIdPolicy.Default.Derive("00000000-0000-0000-0000-000000000000", null, null).Should().BeNull();

    [Fact]
    public void Source_order_is_configurable()
    {
        var policy = GlobalUserIdPolicy.Parse(["externalId", "id", "userUuid"]);

        policy.Derive(Uuid, Other, null).Should().Be(Other);
        policy.Derive(Uuid, null, "22222222-2222-2222-2222-222222222222").Should().Be("22222222-2222-2222-2222-222222222222");
        policy.Derive(Uuid, null, null).Should().Be(Uuid);
    }

    [Fact]
    public void Parse_defaults_when_empty_and_rejects_unknown_names()
    {
        GlobalUserIdPolicy.Parse([]).Order.Should().Equal(GlobalUserIdSource.UserUuid, GlobalUserIdSource.ExternalId);
        var act = () => GlobalUserIdPolicy.Parse(["nonsense"]);
        act.Should().Throw<ArgumentException>();
    }
}
