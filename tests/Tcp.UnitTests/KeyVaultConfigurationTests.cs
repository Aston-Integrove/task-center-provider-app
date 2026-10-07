using Microsoft.Extensions.Configuration;
using Tcp.Api.Configuration;

namespace Tcp.UnitTests;

public class KeyVaultConfigurationTests
{
    [Fact]
    public void Options_bind_from_configuration()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeyVault:Uri"] = "https://kv-test.vault.azure.net/",
            ["Diagnostics:RequestLogSize"] = "42",
            ["Database:MigrateOnStartup"] = "true",
        }).Build();

        cfg.GetSection(KeyVaultOptions.Section).Get<KeyVaultOptions>()!.Uri.Should().Be("https://kv-test.vault.azure.net/");
        cfg.GetSection(DiagnosticsOptions.Section).Get<DiagnosticsOptions>()!.RequestLogSize.Should().Be(42);
        cfg.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>()!.MigrateOnStartup.Should().BeTrue();
    }

    [Fact]
    public void Empty_uri_skips_key_vault_source()
    {
        var bootstrap = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["KeyVault:Uri"] = "" }).Build();
        var builder = new ConfigurationBuilder();

        builder.AddTcpKeyVault(bootstrap);

        builder.Sources.Should().BeEmpty();
    }

    [Theory]
    [InlineData("admin-password", "Admin:Password")]
    [InlineData("oauth-client-tc-tech", "Secrets:oauth-client-tc-tech")]
    [InlineData("token-signing-key", "Secrets:token-signing-key")]
    public void Secret_names_map_to_configuration_keys(string secret, string key) =>
        TcpKeyVaultSecretManager.MapName(secret).Should().Be(key);
}
