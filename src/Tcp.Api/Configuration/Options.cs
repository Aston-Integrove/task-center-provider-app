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

    /// <summary>
    /// SQLite journal mode applied at startup (e.g. WAL, DELETE). Empty keeps the file's current mode. WAL needs shared
    /// memory and does not work on network file systems such as Azure Files (SMB): use DELETE there.
    /// </summary>
    public string JournalMode { get; set; } = "";
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

public sealed class SpiOptions
{
    public const string Section = "Spi";

    /// <summary>Seed the task definitions at startup (after migrations).</summary>
    public bool SeedOnStartup { get; set; } = true;

    /// <summary>Seed file, relative to the application base directory.</summary>
    public string SeedFile { get; set; } = "Seed/task-definitions.json";

    /// <summary>P3: answer POST /response with 202 and complete it in the background.</summary>
    public bool AsyncResponses { get; set; }

    public int AsyncDelaySeconds { get; set; } = 2;

    /// <summary>With AsyncResponses: response codes that fail and surface as operationErrors.</summary>
    public List<string> SimulateFailureCodes { get; set; } = [];
}

public sealed class AppOptions
{
    public const string Section = "App";
    public const string BasicMode = "Basic";
    public const string OidcMode = "IasOidc";

    /// <summary><c>Basic</c> (admin Basic auth, read-only page) or <c>IasOidc</c> (SAP IAS login, entitled users may act).</summary>
    public string Auth { get; set; } = BasicMode;

    public bool IsOidc => string.Equals(Auth, OidcMode, StringComparison.OrdinalIgnoreCase);

    public AppOidcOptions Oidc { get; set; } = new();
}

public sealed class AppOidcOptions
{
    /// <summary>IAS tenant URL, e.g. https://&lt;tenant&gt;.accounts.ondemand.com</summary>
    public string Authority { get; set; } = "";

    public string ClientId { get; set; } = "";

    /// <summary>Client secret; in Azure the Key Vault secret <c>ias-oidc-client-secret</c> (config <c>Secrets:ias-oidc-client-secret</c>).</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Claim carrying the Global User ID; other claims (email, sub) are used as fallbacks by the resolver.</summary>
    public string UserIdClaim { get; set; } = "user_uuid";
}
