using Tcp.Api.Auth;
using Tcp.Api.Auth.Grants;

namespace Tcp.Api.Endpoints.Admin;

public sealed record DecodeAssertionRequest(string? Assertion);

public static class DiagnosticsEndpoints
{
    public static RouteGroupBuilder MapAdminDiagnostics(this RouteGroupBuilder admin)
    {
        // POST /admin/api/diagnostics/decode-assertion  {"assertion":"<JWT>"}
        // Shows header + claims (never the signature) and how we would resolve the Global User ID.
        admin.MapPost("/diagnostics/decode-assertion", async (
            DecodeAssertionRequest request,
            IAssertionIssuerRegistry issuers,
            IGlobalUserResolver resolver,
            CancellationToken ct) =>
        {
            var decoded = AssertionDecoder.TryDecode(request.Assertion);
            if (decoded is null)
                return Results.BadRequest(new { error = "not_a_jwt", detail = "Expected header.payload.signature" });

            var issuer = decoded.Claims["iss"]?.GetValue<string>();
            var trusted = issuer is null ? null : issuers.Find(issuer);
            var resolved = await resolver.ResolveAsync(decoded.Claims, ct);

            return Results.Ok(new
            {
                header = decoded.Header,
                claims = decoded.Claims,
                claimNames = decoded.Claims.Select(c => c.Key).ToArray(),
                issuer,
                trustedIssuer = trusted is not null,
                userResolution = new
                {
                    resolved = resolved is not null,
                    globalUserId = resolved?.User.GlobalUserId,
                    via = resolved?.Via,
                },
            });
        });

        // POST /admin/api/diagnostics/technical-token {"clientId":"tc-tech"}
        // Issues the same token Task Center's destination would get from /oauth/token (client_credentials), so testers
        // can paste it into curl/Postman. Admin-only; never logged.
        admin.MapPost("/diagnostics/technical-token", (TechnicalTokenRequest? request, IClientStore clients, AccessTokenFactory tokens) =>
        {
            var clientId = string.IsNullOrWhiteSpace(request?.ClientId) ? "tc-tech" : request.ClientId;
            var client = clients.Find(clientId);
            if (client is null || !client.AllowedGrants.Contains(GrantTypes.ClientCredentials))
                throw AdminException.Invalid(new AdminError("clientId", $"'{clientId}' is not a client_credentials client"));

            var lifetime = Math.Clamp(client.TokenLifetimeSeconds, 1, Auth.Grants.ClientCredentialsGrantHandler.MaxLifetimeSeconds);
            var issued = tokens.Issue(client, client.ClientId, client.DefaultScopes, lifetime);
            var decoded = AssertionDecoder.TryDecode(issued.AccessToken);
            return Results.Ok(new
            {
                access_token = issued.AccessToken, token_type = "Bearer", expires_in = issued.ExpiresIn, scope = issued.Scope,
                clientId = client.ClientId, header = decoded?.Header, claims = decoded?.Claims,
            });
        });

        // POST /admin/api/diagnostics/simulate-pull {"modifiedAfter":"...","lastId":"...","top":10,"languages":"en-US,de-DE"}
        // Runs exactly what GET /tasks runs (repository + mapper) and shows the JSON Task Center would receive.
        admin.MapPost("/diagnostics/simulate-pull", async (HttpContext http, SimulatePullRequest? request,
            Infrastructure.Tasks.TaskRepository repository, Spi.SpiTaskService spi) =>
        {
            DateTime? after = null;
            if (!string.IsNullOrWhiteSpace(request?.ModifiedAfter))
            {
                if (!DateTime.TryParseExact(request.ModifiedAfter, Spi.SpiMapper.TimestampFormat, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                    throw AdminException.Invalid(new AdminError("modifiedAfter", $"must look like {Spi.SpiMapper.TimestampFormat}"));
                after = parsed;
            }
            if (request?.LastId is not null && after is null)
                throw AdminException.Invalid(new AdminError("lastId", "requires modifiedAfter"));
            var top = Math.Clamp(request?.Top ?? 10, 1, 1000);
            var languages = (request?.Languages ?? "en-US,de-DE").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var page = await repository.PullAsync(new Infrastructure.Tasks.PullQuery(after, request?.LastId, top), http.RequestAborted);
            var json = await spi.MapAsync(page, spi.Context(http, languages), http.RequestAborted);
            watch.Stop();

            return Results.Ok(new System.Text.Json.Nodes.JsonObject
            {
                ["request"] = new System.Text.Json.Nodes.JsonObject
                    { ["modifiedAfter"] = request?.ModifiedAfter, ["lastId"] = request?.LastId, ["top"] = top, ["languages"] = string.Join(',', languages) },
                ["count"] = page.Count,
                ["durationMs"] = watch.ElapsedMilliseconds,
                ["response"] = new System.Text.Json.Nodes.JsonObject { ["value"] = json },
            });
        });

        return admin;
    }
}

public sealed record TechnicalTokenRequest(string? ClientId);

public sealed record SimulatePullRequest(string? ModifiedAfter, string? LastId, int? Top, string? Languages);
