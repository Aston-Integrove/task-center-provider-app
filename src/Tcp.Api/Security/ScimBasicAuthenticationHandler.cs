using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Tcp.Api.Auth;

namespace Tcp.Api.Security;

/// <summary>
/// Optional HTTP Basic for /scim (<c>Scim:AllowBasic</c>, FR-SCIM-11): authenticates a registered OAuth client
/// that is allowed the <c>scim</c> scope, using the same secrets as the token endpoint.
/// </summary>
public sealed class ScimBasicAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IClientStore clients)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var basic = ClientAuthenticator.ReadBasic(Request);
        if (basic is null) return Task.FromResult(AuthenticateResult.NoResult());
        var (id, secret) = basic.Value;
        if (string.IsNullOrEmpty(id) || secret is null) return Task.FromResult(AuthenticateResult.Fail("Malformed credentials"));

        var client = clients.Find(id);
        var expected = client is null ? null : clients.GetSecret(client);
        var ok = ConstantTime.Equals(secret, expected ?? "dummy-secret-for-constant-time-path");
        if (client is null || expected is null || !ok || !client.AllowedScopes.Contains(Scopes.Scim))
            return Task.FromResult(AuthenticateResult.Fail("Invalid credentials"));

        var identity = new ClaimsIdentity(
            [new Claim("client_id", client.ClientId), new Claim("sub", client.ClientId), new Claim("scope", Scopes.Scim)],
            Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.Append("WWW-Authenticate", "Basic realm=\"scim\", charset=\"UTF-8\"");
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
