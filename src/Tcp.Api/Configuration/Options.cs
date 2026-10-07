namespace Tcp.Api.Configuration;

public sealed class ProviderOptions
{
    public const string Section = "Provider";
    public string ApplicationId { get; set; } = "integrove";
    public string ApplicationInstanceId { get; set; } = "tcproto";
    public string TenantId { get; set; } = "dev";
    public string PublicBaseUrl { get; set; } = "";
    public string DefaultLanguage { get; set; } = "en-US";
}

public sealed class DiagnosticsOptions
{
    public const string Section = "Diagnostics";
    public bool RequestLogEnabled { get; set; } = true;
    public int RequestLogSize { get; set; } = 500;
    public bool LogAssertionClaims { get; set; }
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";
    public bool MigrateOnStartup { get; set; }
}

public sealed class KeyVaultOptions
{
    public const string Section = "KeyVault";

    /// <summary>Vault URI, e.g. https://kv-tcp-dev.vault.azure.net/. Empty = Key Vault disabled (local dev).</summary>
    public string Uri { get; set; } = "";
}

public sealed class AdminOptions
{
    public const string Section = "Admin";
    public string BasicUser { get; set; } = "admin";

    /// <summary>Admin password. In Azure this comes from the Key Vault secret <c>admin-password</c>.</summary>
    public string Password { get; set; } = "";
}
