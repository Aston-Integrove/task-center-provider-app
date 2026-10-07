using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Tcp.Api.Auth.Grants;
using Tcp.Api.Configuration;
using Tcp.Api.Security;
using Tcp.Domain.Identity;

namespace Tcp.Api.Auth;

public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddTcpAuth(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<OAuthOptions>()
            .Bind(config.GetSection(OAuthOptions.Section))
            .PostConfigure<IOptions<ProviderOptions>>((o, provider) =>
            {
                if (string.IsNullOrWhiteSpace(o.Issuer))
                    o.Issuer = string.IsNullOrWhiteSpace(provider.Value.PublicBaseUrl) ? "http://localhost" : provider.Value.PublicBaseUrl;
                o.Issuer = o.Issuer.TrimEnd('/');
            });
        services.Configure<SamlOptions>(config.GetSection(SamlOptions.Section));
        services.Configure<FormOptions>(o =>
        {
            o.ValueLengthLimit = 26 * 1024;
            o.KeyLengthLimit = 1024;
            o.ValueCountLimit = 32;
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IUserDirectory, EmptyUserDirectory>();

        services.AddSingleton<ISigningKeyProvider>(sp =>
        {
            var oauth = sp.GetRequiredService<IOptions<OAuthOptions>>().Value;
            var cfg = sp.GetRequiredService<IConfiguration>();
            var env = sp.GetRequiredService<IHostEnvironment>();
            return new SigningKeyProvider(
                cfg[$"Secrets:{oauth.SigningKeySecretName}"],
                cfg[$"Secrets:{oauth.PreviousSigningKeySecretName}"],
                allowEphemeral: oauth.AllowEphemeralSigningKey || !env.IsProduction(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<SigningKeyProvider>());
        });
        services.AddSingleton<AccessTokenFactory>();
        services.AddSingleton<IClientStore, ConfigClientStore>();
        services.AddSingleton<ClientAuthenticator>();
        services.AddSingleton<TokenRateLimiter>();
        services.AddSingleton<IAssertionIssuerRegistry, AssertionIssuerRegistry>();
        services.AddHttpClient(AssertionIssuerRegistry.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5));

        services.AddScoped<IGlobalUserResolver, GlobalUserResolver>();
        services.AddScoped<IGrantHandler, ClientCredentialsGrantHandler>();
        services.AddScoped<IGrantHandler, JwtBearerGrantHandler>();
        if (config.GetValue<bool>($"{SamlOptions.Section}:Enabled"))
            services.AddScoped<IGrantHandler, Saml2BearerGrantHandler>();

        services.AddScoped<ICurrentUser, CurrentUser>();

        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, BearerOptionsSetup>();
        services.AddAuthentication(AuthSchemes.Bearer)
            .AddJwtBearer(AuthSchemes.Bearer, _ => { })
            .AddScheme<AuthenticationSchemeOptions, AdminBasicAuthenticationHandler>(AuthSchemes.AdminBasic, null);

        services.AddSingleton<IAuthorizationHandler, ScopeRequirementHandler>();
        services.AddSingleton<IAuthorizationHandler, UserContextRequirementHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, SpiAuthorizationResultHandler>();

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(AuthSchemes.Bearer).RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.Admin, p => p.AddAuthenticationSchemes(AuthSchemes.AdminBasic).RequireAuthenticatedUser())
            .AddPolicy(Policies.SpiTech, p => p.AddAuthenticationSchemes(AuthSchemes.Bearer)
                .RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(Scopes.SpiTech)))
            .AddPolicy(Policies.SpiUser, p => p.AddAuthenticationSchemes(AuthSchemes.Bearer)
                .RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(Scopes.SpiUser), new UserContextRequirement()))
            .AddPolicy(Policies.SpiAny, p => p.AddAuthenticationSchemes(AuthSchemes.Bearer)
                .RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(Scopes.SpiTech, Scopes.SpiUser)))
            .AddPolicy(Policies.Scim, p => p.AddAuthenticationSchemes(AuthSchemes.Bearer)
                .RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(Scopes.Scim)));

        return services;
    }
}
