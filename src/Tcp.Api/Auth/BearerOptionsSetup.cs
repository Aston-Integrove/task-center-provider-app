using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Tcp.Api.Security;

namespace Tcp.Api.Auth;

/// <summary>
/// Validates the tokens this service issues (FR-RES-01): our issuer, one of our client audiences, our keys,
/// RS256 only. Resolved lazily so Key Vault configuration is loaded before the signing key is read.
/// </summary>
public sealed class BearerOptionsSetup(ISigningKeyProvider keys, IOptions<OAuthOptions> oauth) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != AuthSchemes.Bearer) return;

        var audiences = oauth.Value.Clients.Select(c => c.Audience).Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList();

        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = false;
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                if (!Endpoints.Spi.SpiPaths.IsSpi(context.Request.Path)) return;
                context.HandleResponse();
                context.Response.Headers.WWWAuthenticate = context.AuthenticateFailure is null ? "Bearer" : "Bearer error=\"invalid_token\"";
                await Endpoints.Spi.SpiErrors.WriteAsync(context.HttpContext, Domain.Tasks.SpiCodes.Unauthorized);
            },
        };
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = oauth.Value.Issuer,
            ValidateAudience = true,
            ValidAudiences = audiences,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys.ValidationKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    }
}
