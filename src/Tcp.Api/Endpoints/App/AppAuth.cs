using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Tcp.Api.Configuration;
using Tcp.Api.Security;

namespace Tcp.Api.Endpoints.App;

public static class AppAuth
{
    public const string CallbackPath = "/app/signin-oidc";

    /// <summary>
    /// "Open in App" authentication (US-005-6.2). <c>Basic</c>: the admin Basic credentials. <c>IasOidc</c>: authorization
    /// code flow with PKCE against SAP IAS; the OIDC scheme signs the user into a short-lived cookie session.
    /// </summary>
    public static IServiceCollection AddTcpAppAuth(this IServiceCollection services, IConfiguration config)
    {
        var app = config.GetSection(AppOptions.Section).Get<AppOptions>() ?? new AppOptions();
        services.Configure<AppOptions>(config.GetSection(AppOptions.Section));
        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = ".tcp.antiforgery";
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.HttpOnly = true;
            o.FormFieldName = "__RequestVerificationToken";
        });

        if (!app.IsOidc)
        {
            services.AddAuthorizationBuilder().AddPolicy(Policies.App, p =>
                p.AddAuthenticationSchemes(AuthSchemes.AdminBasic).RequireAuthenticatedUser());
            return services;
        }

        var secret = config["Secrets:ias-oidc-client-secret"] ?? app.Oidc.ClientSecret;
        services.AddAuthentication()
            .AddCookie(AuthSchemes.AppCookie, o =>
            {
                o.Cookie.Name = ".tcp.app";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax; // must survive the redirect back from IAS
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(1);
                o.SlidingExpiration = false;
                // API-style responses for the POST endpoints; the page itself is challenged through the OIDC scheme.
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
            })
            .AddOpenIdConnect(AuthSchemes.AppOidc, o =>
            {
                o.SignInScheme = AuthSchemes.AppCookie;
                o.Authority = app.Oidc.Authority;
                o.ClientId = app.Oidc.ClientId;
                o.ClientSecret = secret;
                o.ResponseType = OpenIdConnectResponseType.Code;
                o.UsePkce = true;
                // Query (not form_post): the callback is a plain GET redirect, which works with SameSite=Lax cookies.
                o.ResponseMode = OpenIdConnectResponseMode.Query;
                // Defaults are SameSite=None + Secure (needed for form_post). With a GET callback Lax is enough, and
                // SameAsRequest keeps local http development working; behind the Azure proxy requests are https.
                foreach (var cookie in new[] { o.CorrelationCookie, o.NonceCookie })
                {
                    cookie.SameSite = SameSiteMode.Lax;
                    cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                }
                o.CallbackPath = CallbackPath;
                o.SignedOutCallbackPath = "/app/signout-callback-oidc";
                o.Scope.Clear();
                foreach (var scope in new[] { "openid", "email", "profile" }) o.Scope.Add(scope);
                o.SaveTokens = false;
                o.GetClaimsFromUserInfoEndpoint = false;
                o.MapInboundClaims = false;
                o.RequireHttpsMetadata = !(app.Oidc.Authority.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
                o.TokenValidationParameters.NameClaimType = "name";
            });

        services.AddAuthorizationBuilder().AddPolicy(Policies.App, p =>
            p.AddAuthenticationSchemes(AuthSchemes.AppOidc).RequireAuthenticatedUser());
        return services;
    }
}
