using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using Xunit;

namespace Tcp.TestSupport;

/// <summary>One SQL Server 2022 container shared by all tests in a collection.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString(string database) =>
        new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_container.GetConnectionString())
        { InitialCatalog = database }.ConnectionString;

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
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
    public const string UnreachableSql = "Server=127.0.0.1,1;Database=x;User Id=sa;Password=x;Connect Timeout=2;TrustServerCertificate=True";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Sql", connectionString ?? UnreachableSql);
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
