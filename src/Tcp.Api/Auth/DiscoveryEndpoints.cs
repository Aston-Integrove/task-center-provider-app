using Microsoft.Extensions.Options;

namespace Tcp.Api.Auth;

public static class DiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapOAuthDiscovery(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/jwks.json", (ISigningKeyProvider keys) =>
            Results.Json(new { keys = keys.PublicKeys })).AllowAnonymous();

        app.MapGet("/.well-known/openid-configuration", (IOptions<OAuthOptions> options, IEnumerable<Grants.IGrantHandler> handlers) =>
        {
            var issuer = options.Value.Issuer.TrimEnd('/');
            return Results.Json(new
            {
                issuer,
                token_endpoint = issuer + TokenEndpoint.Path,
                jwks_uri = issuer + "/.well-known/jwks.json",
                grant_types_supported = handlers.Select(h => h.GrantType).ToArray(),
                token_endpoint_auth_methods_supported = new[] { "client_secret_basic", "client_secret_post" },
                response_types_supported = Array.Empty<string>(),
                id_token_signing_alg_values_supported = new[] { "RS256" },
                subject_types_supported = new[] { "public" },
            });
        }).AllowAnonymous();

        return app;
    }
}
