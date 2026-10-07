using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.MsSql;

namespace Tcp.IntegrationTests;

/// <summary>One SQL Server 2022 container shared by all integration tests.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString(string database) =>
        new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_container.GetConnectionString())
        { InitialCatalog = database }.ConnectionString;

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql";
}

public sealed class TcpFactory(string connectionString, bool migrate = true, Dictionary<string, string?>? extra = null)
    : WebApplicationFactory<Program>
{
    public const string AdminUser = "admin";
    public const string AdminPassword = "test-admin-pw";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Sql", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", migrate ? "true" : "false");
        builder.UseSetting("Admin:BasicUser", AdminUser);
        builder.UseSetting("Admin:Password", AdminPassword);
        builder.UseSetting("Provider:PublicBaseUrl", "https://tc.test");
        foreach (var (k, v) in extra ?? [])
            builder.UseSetting(k, v);
    }
}
