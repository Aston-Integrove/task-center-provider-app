using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Tcp.Api.Configuration;

namespace Tcp.Api.Security;

public static class AuthSchemes
{
    public const string Bearer = "Bearer";
    public const string AdminBasic = "AdminBasic";
}

public static class Policies
{
    public const string Admin = "admin";
}

/// <summary>HTTP Basic authentication for the admin API (prototype). Fails closed when no password is configured.</summary>
public sealed class AdminBasicAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptions<AdminOptions> adminOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!AuthenticationHeaderValue.TryParse(header, out var parsed) ||
            !string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(parsed.Parameter))
            return Task.FromResult(AuthenticateResult.NoResult());

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter));
        }
        catch (FormatException)
        {
            return Task.FromResult(AuthenticateResult.Fail("Malformed credentials"));
        }

        var idx = decoded.IndexOf(':');
        if (idx < 0) return Task.FromResult(AuthenticateResult.Fail("Malformed credentials"));

        var user = decoded[..idx];
        var password = decoded[(idx + 1)..];
        var cfg = adminOptions.Value;

        if (string.IsNullOrEmpty(cfg.Password))
            return Task.FromResult(AuthenticateResult.Fail("Admin access not configured"));

        // Evaluate both comparisons so timing does not reveal which one failed.
        var userOk = ConstantTimeEquals(user, cfg.BasicUser);
        var passOk = ConstantTimeEquals(password, cfg.Password);
        if (!(userOk & passOk))
            return Task.FromResult(AuthenticateResult.Fail("Invalid credentials"));

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, user), new Claim("role", "admin")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Basic realm=\"admin\", charset=\"UTF-8\"";
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    internal static bool ConstantTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)),
                                                SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
