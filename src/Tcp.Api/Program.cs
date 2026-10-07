using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tcp.Api.Auth;
using Tcp.Api.Configuration;
using Tcp.Api.Endpoints;
using Tcp.Api.Diagnostics;
using Tcp.Api.Endpoints.Admin;
using Tcp.Api.Health;
using Tcp.Api.Security;
using Tcp.Api.Endpoints.Scim;
using Tcp.Infrastructure.Persistence;
using Tcp.Infrastructure.Scim;

var builder = WebApplication.CreateBuilder(args);

// Key Vault is a configuration source only when KeyVault:Uri is set (never locally).
builder.Configuration.AddTcpKeyVault(builder.Configuration);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    o.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});

var config = builder.Configuration;
builder.Services.Configure<ProviderOptions>(config.GetSection(ProviderOptions.Section));
builder.Services.Configure<DiagnosticsOptions>(config.GetSection(DiagnosticsOptions.Section));
builder.Services.Configure<DatabaseOptions>(config.GetSection(DatabaseOptions.Section));
builder.Services.Configure<KeyVaultOptions>(config.GetSection(KeyVaultOptions.Section));
builder.Services.Configure<AdminOptions>(config.GetSection(AdminOptions.Section));

builder.Services.AddDbContext<TcpDbContext>(o =>
    o.UseSqlServer(config.GetConnectionString("Sql") ?? throw new InvalidOperationException("ConnectionStrings:Sql is not configured")));

builder.Services.AddSingleton<IRequestLog>(sp =>
    new RequestLogBuffer(sp.GetRequiredService<IOptions<DiagnosticsOptions>>().Value.RequestLogSize));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddTcpHealth();

builder.Services.AddTcpAuth(config);
builder.Services.Configure<ScimOptions>(config.GetSection(ScimOptions.Section));
builder.Services.AddScimInfrastructure();

var app = builder.Build();

if (config.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>()?.MigrateOnStartup == true)
{
    // A failed migration throws and stops the host: the container never serves traffic on a stale schema.
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TcpDbContext>();
    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    app.Logger.LogInformation("Applying {Count} pending migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
    await db.Database.MigrateAsync();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<RequestLogMiddleware>();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment()) app.UseHsts();

app.UseAuthentication();
app.UseAuthorization();

app.MapTcpHealth();
app.MapOAuthDiscovery();
app.MapTokenEndpoint();
app.MapScim();
app.MapAdminApi().MapAdminDiagnostics();
foreach (var module in app.Services.GetServices<IEndpointModule>()) module.Map(app);

app.Run();

public partial class Program;
