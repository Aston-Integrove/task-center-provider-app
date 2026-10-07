using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Tcp.Api.Configuration;
using Tcp.Api.Diagnostics;
using Tcp.Api.Endpoints.Admin;
using Tcp.Api.Health;
using Tcp.Api.Security;
using Tcp.Infrastructure.Persistence;

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

// Authentication skeleton: Bearer validates tokens we issue (keys wired in feature 002; until then every
// bearer token is rejected, i.e. fail closed). AdminBasic protects /admin/api.
builder.Services
    .AddAuthentication(AuthSchemes.Bearer)
    .AddJwtBearer(AuthSchemes.Bearer, o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = config["OAuth:Issuer"] ?? config["Provider:PublicBaseUrl"] ?? "unconfigured",
            ValidateAudience = false,
            ValidateLifetime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    })
    .AddScheme<AuthenticationSchemeOptions, AdminBasicAuthenticationHandler>(AuthSchemes.AdminBasic, null);

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder(AuthSchemes.Bearer).RequireAuthenticatedUser().Build())
    .AddPolicy(Policies.Admin, p => p.AddAuthenticationSchemes(AuthSchemes.AdminBasic).RequireAuthenticatedUser());

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
app.MapAdminApi();

app.Run();

public partial class Program;
