using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Tcp.Api.Auth.Grants;

namespace Tcp.Api.Auth;

public sealed record OAuthErrorBody(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorDescription);

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("scope")] string Scope);

/// <summary>POST /oauth/token (RFC 6749). Anonymous: the endpoint authenticates the client itself.</summary>
public static class TokenEndpoint
{
    public const string Path = "/oauth/token";
    public const int MaxFormBytes = 32 * 1024;

    public static IEndpointRouteBuilder MapTokenEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, HandleAsync).AllowAnonymous().DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        [FromServices] ClientAuthenticator authenticator,
        [FromServices] IEnumerable<IGrantHandler> handlers,
        [FromServices] AccessTokenFactory tokens,
        [FromServices] TokenRateLimiter limiter,
        [FromServices] ILogger<TokenResult> logger)
    {
        var request = http.Request;
        NoStore(http);

        if (request.ContentType is null ||
            !request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return Error(http, "invalid_request", "Content-Type must be application/x-www-form-urlencoded");

        if (request.ContentLength > MaxFormBytes)
            return Error(http, "invalid_request", "Request body too large");

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(http.RequestAborted);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
        {
            return Error(http, "invalid_request", "Malformed or oversized form body");
        }

        // Throttle before checking the secret so brute-forcing is bounded per claimed client_id.
        var claimedId = ClientAuthenticator.ReadBasic(request)?.Id ?? form["client_id"].FirstOrDefault();
        var limiterKey = string.IsNullOrEmpty(claimedId)
            ? "anon:" + http.Connection.RemoteIpAddress
            : "client:" + (claimedId.Length > 128 ? claimedId[..128] : claimedId);
        if (!limiter.TryAcquire(limiterKey))
        {
            http.Response.Headers.RetryAfter = "60";
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        var auth = authenticator.Authenticate(request, form);
        if (!auth.Succeeded)
        {
            logger.LogWarning("Client authentication failed ({Error}) for client {ClientId}",
                auth.Error, Sanitize(claimedId));
            if (auth.Status == StatusCodes.Status401Unauthorized)
                http.Response.Headers.WWWAuthenticate = "Basic realm=\"oauth\", charset=\"UTF-8\"";
            return Error(http, auth.Error!, auth.Description, auth.Status);
        }
        var client = auth.Client!;

        var grantType = form["grant_type"].FirstOrDefault();
        if (string.IsNullOrEmpty(grantType))
            return Error(http, "invalid_request", "grant_type is required");

        var handler = handlers.FirstOrDefault(h => string.Equals(h.GrantType, grantType, StringComparison.Ordinal));
        if (handler is null)
            return Error(http, "unsupported_grant_type", "Grant type is not supported");
        if (!client.AllowedGrants.Contains(grantType, StringComparer.Ordinal))
            return Error(http, "unauthorized_client", "Client is not allowed to use this grant type");

        var requested = (form["scope"].FirstOrDefault() ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var scopes = requested.Length > 0 ? requested.Distinct().ToList() : [.. client.DefaultScopes];
        if (scopes.Count == 0 || scopes.Any(s => !client.AllowedScopes.Contains(s, StringComparer.Ordinal)))
            return Error(http, "invalid_scope", "Requested scope is not allowed for this client");

        var result = await handler.HandleAsync(new GrantContext(client, form, scopes, http.RequestAborted));
        if (!result.Succeeded)
            return Error(http, result.Error!, result.ErrorDescription);

        var issued = tokens.Issue(client, result.Subject!, scopes, result.LifetimeSeconds, result.ExtraClaims);
        return Results.Json(new TokenResponse(issued.AccessToken, "Bearer", issued.ExpiresIn, issued.Scope));
    }

    private static void NoStore(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
    }

    private static IResult Error(HttpContext http, string error, string? description, int status = StatusCodes.Status400BadRequest)
    {
        NoStore(http);
        return Results.Json(new OAuthErrorBody(error, description), statusCode: status);
    }

    private static string Sanitize(string? value) =>
        value is null ? "(none)" : new string(value.Take(64).Where(c => !char.IsControl(c)).ToArray());

    /// <summary>Logger category marker.</summary>
    public sealed class TokenResult;
}
