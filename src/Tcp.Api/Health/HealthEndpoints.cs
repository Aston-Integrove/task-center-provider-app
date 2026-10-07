using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Tcp.Api.Configuration;
using Tcp.Infrastructure.Persistence;

namespace Tcp.Api.Health;

public sealed class DatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TcpDbContext>();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            return await db.Database.CanConnectAsync(cts.Token)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database unreachable");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Database unreachable", ex);
        }
    }
}

public sealed class KeyVaultHealthCheck(IOptions<KeyVaultOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Value.Uri))
            return HealthCheckResult.Healthy("Key Vault not configured");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var client = KeyVaultConfigurationExtensions.CreateSecretClient(options.Value);
            await foreach (var _ in client.GetPropertiesOfSecretsAsync(cts.Token)) break;
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Key Vault unreachable", ex);
        }
    }
}

public static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddTcpHealth(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag])
            .AddCheck<KeyVaultHealthCheck>("keyvault", tags: [ReadyTag]);
        return services;
    }

    public static IEndpointRouteBuilder MapTcpHealth(this IEndpointRouteBuilder app)
    {
        // Liveness: no checks, always healthy while the process serves requests.
        app.MapHealthChecks("/healthz", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteStatus,
        }).AllowAnonymous();

        // Readiness: DB + Key Vault reachable, otherwise 503.
        app.MapHealthChecks("/healthz/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains(ReadyTag),
            ResponseWriter = WriteStatus,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        }).AllowAnonymous();

        return app;
    }

    private static Task WriteStatus(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { status = report.Status.ToString() }));
    }
}
