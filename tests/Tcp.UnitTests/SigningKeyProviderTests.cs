using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Tcp.Api.Auth;

namespace Tcp.UnitTests;

public class SigningKeyProviderTests
{
    private static string NewPem() => RSA.Create(2048).ExportPkcs8PrivateKeyPem();

    [Fact] // T002-01
    public void Kid_is_stable_for_the_same_key()
    {
        var pem = NewPem();
        var a = new SigningKeyProvider(pem, null, false, NullLogger.Instance);
        var b = new SigningKeyProvider(pem, null, false, NullLogger.Instance);

        a.Current.Kid.Should().NotBeNullOrEmpty().And.Be(b.Current.Kid);
    }

    [Fact] // T002-01
    public void Different_keys_have_different_kids()
    {
        var a = new SigningKeyProvider(NewPem(), null, false, NullLogger.Instance);
        var b = new SigningKeyProvider(NewPem(), null, false, NullLogger.Instance);
        a.Current.Kid.Should().NotBe(b.Current.Kid);
    }

    [Fact] // T002-01
    public void Jwks_contains_current_and_previous_key_without_private_material()
    {
        var provider = new SigningKeyProvider(NewPem(), NewPem(), false, NullLogger.Instance);

        provider.PublicKeys.Should().HaveCount(2);
        provider.PublicKeys[0].Kid.Should().Be(provider.Current.Kid);
        provider.ValidationKeys.Select(k => k.KeyId).Should().Equal(provider.PublicKeys.Select(k => k.Kid));
        provider.PublicKeys.Should().OnlyContain(k => k.Kty == "RSA" && k.Use == "sig" && k.Alg == "RS256");
        System.Text.Json.JsonSerializer.Serialize(provider.PublicKeys).Should().NotContain("\"d\"").And.NotContain("\"p\"");
    }

    [Fact] // FR-KEY-02
    public void Missing_key_generates_ephemeral_key_only_when_allowed()
    {
        var ephemeral = new SigningKeyProvider(null, null, true, NullLogger.Instance);
        ephemeral.PublicKeys.Should().ContainSingle();

        var act = () => new SigningKeyProvider(null, null, false, NullLogger.Instance);
        act.Should().Throw<InvalidOperationException>();
    }
}
