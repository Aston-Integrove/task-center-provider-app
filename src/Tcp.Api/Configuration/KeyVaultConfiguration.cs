using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace Tcp.Api.Configuration;

/// <summary>
/// Maps every Key Vault secret to <c>Secrets:{name}</c> (e.g. <c>oauth-client-tc-tech</c> becomes
/// <c>Secrets:oauth-client-tc-tech</c>) and the admin password to <c>Admin:Password</c>.
/// </summary>
public sealed class TcpKeyVaultSecretManager : KeyVaultSecretManager
{
    public const string AdminPasswordSecret = "admin-password";

    public override string GetKey(KeyVaultSecret secret) => MapName(secret.Name);

    public static string MapName(string secretName) =>
        secretName == AdminPasswordSecret ? "Admin:Password" : $"Secrets:{secretName}";
}

public static class KeyVaultConfigurationExtensions
{
    /// <summary>Adds Key Vault as a configuration source; no-op when <c>KeyVault:Uri</c> is empty (local dev).</summary>
    public static IConfigurationBuilder AddTcpKeyVault(this IConfigurationBuilder builder, IConfiguration bootstrap)
    {
        var options = bootstrap.GetSection(KeyVaultOptions.Section).Get<KeyVaultOptions>() ?? new KeyVaultOptions();
        if (string.IsNullOrWhiteSpace(options.Uri)) return builder;

        builder.AddAzureKeyVault(new Uri(options.Uri), new DefaultAzureCredential(), new TcpKeyVaultSecretManager());
        return builder;
    }

    public static SecretClient CreateSecretClient(KeyVaultOptions options) =>
        new(new Uri(options.Uri), new DefaultAzureCredential());
}
