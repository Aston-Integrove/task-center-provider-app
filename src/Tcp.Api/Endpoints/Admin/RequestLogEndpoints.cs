using System.Text.Json;
using System.Text.Json.Nodes;
using Tcp.Api.Diagnostics;
using Tcp.Api.Security;
using Tcp.Domain.Tasks;

namespace Tcp.Api.Endpoints.Admin;

public static class RequestLogEndpoints
{
    /// <summary>Admin failures become RFC 9457 problem details with an <c>errors</c> list naming the offending fields.</summary>
    private static async ValueTask<object?> ErrorFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (AdminException ex)
        {
            return Problem(ex.Status, ex.Message, ex.Errors);
        }
        catch (TaskRuleViolation violation)
        {
            var ex = AdminException.FromRule(violation);
            return Problem(ex.Status, ex.Message, ex.Errors);
        }
    }

    private static IResult Problem(int status, string title, IReadOnlyList<AdminError> errors) =>
        Results.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?>
        {
            ["errors"] = errors.Select(e => new { field = e.Field, message = e.Message }).ToArray(),
        });

    public static RouteGroupBuilder MapAdminApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/api")
            .RequireAuthorization(Policies.Admin);
        group.AddEndpointFilter(ErrorFilter);

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
