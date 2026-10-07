using Tcp.Api.Configuration;

namespace Tcp.Api.Endpoints.Spi;

public static class SpiServiceCollectionExtensions
{
    public static IServiceCollection AddSpi(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SpiOptions>(config.GetSection(SpiOptions.Section));
        services.AddScoped<OperationService>();
        services.AddScoped<SpiTaskService>();
        services.AddSingleton<IPendingResponseQueue, PendingResponseQueue>();
        services.AddHostedService<AsyncResponseProcessor>();
        services.AddSingleton<Tcp.Infrastructure.Html.HtmlDescriptionSanitizer>();
        services.AddScoped<Admin.AdminTaskService>();
        return services;
    }
}
