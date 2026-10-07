using System.Text.Json;
using System.Text.Json.Nodes;
using Tcp.Api.Diagnostics;
using Tcp.Api.Security;

namespace Tcp.Api.Endpoints.Admin;

public static class RequestLogEndpoints
{
    public static RouteGroupBuilder MapAdminApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/api")
            .RequireAuthorization(Policies.Admin);

        // GET /admin/api/requests?prefix=/task-provider&status=200&limit=50
        group.MapGet("/requests", (IRequestLog log, string? prefix, int? status, int? limit) =>
            Results.Ok(log.Snapshot(prefix, status, limit)));

        // GET /admin/api/requests/export?prefix=/scim  (spike S-03)
        // Turns the (already redacted) request log into replayable fixtures, oldest first. Save the output under
        // tests/fixtures/ips/ after reviewing it for personal data.
        group.MapGet("/requests/export", (IRequestLog log, string? prefix) =>
        {
            var steps = log.Snapshot(prefix).Reverse().Select(e =>
            {
                e.RequestHeaders.TryGetValue("Content-Type", out var contentType);
                JsonNode? body = null;
                if (!string.IsNullOrWhiteSpace(e.RequestBody))
                {
                    try { body = JsonNode.Parse(e.RequestBody); }
                    catch (JsonException) { body = e.RequestBody; }
                }
                return new JsonObject
                {
                    ["name"] = $"{e.Method} {e.Path}",
                    ["method"] = e.Method,
                    ["path"] = e.Path + e.Query,
                    ["contentType"] = contentType,
                    ["body"] = body,
                    ["expect"] = new JsonObject { ["status"] = e.Status },
                };
            });
            return Results.Ok(new JsonObject
            {
                ["description"] = $"Exported {DateTimeOffset.UtcNow:O}",
                ["steps"] = new JsonArray(steps.Cast<JsonNode>().ToArray()),
            });
        });

        return group;
    }
}
