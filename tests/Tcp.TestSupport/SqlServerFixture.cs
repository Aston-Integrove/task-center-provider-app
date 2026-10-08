using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Tcp.TestSupport;

/// <summary>
/// A temporary folder holding one SQLite file per test database. Kept under its historical name so the many test
/// classes that take it as a constructor argument stay unchanged; no container is needed any more.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tcp-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Connection string for a database file named after <paramref name="database"/> (created on first use).</summary>
    public string ConnectionString(string database) =>
        $"Data Source={Path.Combine(_directory, database + ".db")}";

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best effort */ }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Test host for the API. Without a connection string the database is unreachable and migrations are off,
/// which is fine for tests that never touch it (token service, discovery, auth).
/// </summary>
public sealed class TcpFactory(
    string? connectionString = null,
    Dictionary<string, string?>? settings = null,
    Action<IServiceCollection>? configureServices = null,
    IEnumerable<TestIssuer>? issuers = null,
    bool? migrate = null)
    : WebApplicationFactory<Program>
{
    public const string AdminUser = "admin";
    public const string AdminPassword = "test-admin-pw";
    public const string TechSecret = "tech-secret";
    public const string PpSecret = "pp-secret";
    public const string ScimSecret = "scim-secret";
    public const string PublicBaseUrl = "https://tc.test";

    /// <summary>A database file in a folder that does not exist and is never created (ReadOnly mode never creates it).</summary>
    public static readonly string UnreachableDb = "Data Source=" + Path.Combine(Path.GetTempPath(), "tcp-does-not-exist", "x.db") + ";Mode=ReadOnly";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Database", connectionString ?? UnreachableDb);
        builder.UseSetting("Database:MigrateOnStartup", (migrate ?? connectionString is not null) ? "true" : "false");
        builder.UseSetting("Admin:BasicUser", AdminUser);
        builder.UseSetting("Admin:Password", AdminPassword);
        builder.UseSetting("Provider:PublicBaseUrl", PublicBaseUrl);
        builder.UseSetting("Secrets:oauth-client-tc-tech", TechSecret);
        builder.UseSetting("Secrets:oauth-client-tc-pp", PpSecret);
        builder.UseSetting("Secrets:oauth-client-ips-scim", ScimSecret);
        builder.UseSetting("OAuth:TokenRateLimitPerMinute", "1000");
        builder.UseSetting("OAuth:TrustedAssertionIssuers:0:Issuer", TestIssuer.OidcIssuer);
        builder.UseSetting("OAuth:TrustedAssertionIssuers:0:Type", "Oidc");
        builder.UseSetting("OAuth:TrustedAssertionIssuers:1:Issuer", TestIssuer.XsuaaIssuer);
        builder.UseSetting("OAuth:TrustedAssertionIssuers:1:Type", "Xsuaa");
        foreach (var (k, v) in settings ?? [])
            builder.UseSetting(k, v);

        var fakeIssuers = issuers?.ToList() ?? [];
        builder.ConfigureServices(services =>
        {
            if (fakeIssuers.Count > 0)
            {
                services.AddHttpClient("assertion-issuers")
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeIssuerHandler(fakeIssuers));
            }
            services.AddSingleton<Tcp.Api.Endpoints.IEndpointModule, ProbeEndpoints>();
            configureServices?.Invoke(services);
        });
    }
}
