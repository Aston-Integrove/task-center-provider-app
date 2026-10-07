using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Tcp.Api.Configuration;
using Tcp.Api.Security;
using Tcp.Infrastructure.Scim;

namespace Tcp.Api.Endpoints.Scim;

public static class ScimEndpoints
{
    public const string BasePath = "/scim/v2";

    public static IEndpointRouteBuilder MapScim(this IEndpointRouteBuilder app)
    {
        var scim = app.MapGroup(BasePath).RequireAuthorization(Policies.Scim);
        scim.AddEndpointFilter(ErrorFilter);

        scim.MapScimDiscovery();
        MapUsers(scim);
        MapGroups(scim);
        return app;
    }

    // ---- users --------------------------------------------------------------------------------

    private static void MapUsers(RouteGroupBuilder scim)
    {
        scim.MapGet("/Users", async (HttpContext http, ScimUserService users, IOptions<ScimOptions> opt) =>
        {
            var (start, count) = Paging(http.Request, opt.Value);
            var page = await users.ListAsync(http.Request.Query["filter"].FirstOrDefault(), start, count, http.RequestAborted);
            var baseUrl = BaseUrl(http);
            return ScimResults.Json(200, ListResponse(page.Total, start, page.Items.Select(u => ScimUserMapper.ToJson(u, baseUrl)), http.Request));
        });

        scim.MapPost("/Users", async (HttpContext http, ScimUserService users) =>
        {
            var created = await users.CreateAsync(await ScimResults.ReadBodyAsync(http.Request), ClientOf(http), http.RequestAborted);
            var json = ScimUserMapper.ToJson(created, BaseUrl(http));
            return ScimResults.Json(201, Project(json, http.Request), json["meta"]!["location"]!.GetValue<string>());
        });

        scim.MapGet("/Users/{id}", async (HttpContext http, string id, ScimUserService users) =>
        {
            var user = Guid.TryParse(id, out var guid) ? await users.FindAsync(guid, http.RequestAborted) : null;
            return user is null
                ? ScimResults.Error(404, null, $"User {id} not found")
                : ScimResults.Json(200, Project(ScimUserMapper.ToJson(user, BaseUrl(http)), http.Request));
        });

        scim.MapPut("/Users/{id}", async (HttpContext http, string id, ScimUserService users) =>
        {
            var updated = await users.ReplaceAsync(ParseId(id, "User"), await ScimResults.ReadBodyAsync(http.Request), ClientOf(http), http.RequestAborted);
            return ScimResults.Json(200, Project(ScimUserMapper.ToJson(updated, BaseUrl(http)), http.Request));
        });

        scim.MapMethods("/Users/{id}", ["PATCH"], async (HttpContext http, string id, ScimUserService users, IOptions<ScimOptions> opt) =>
        {
            var updated = await users.PatchAsync(ParseId(id, "User"), await ScimResults.ReadBodyAsync(http.Request), ClientOf(http), http.RequestAborted);
            return opt.Value.PatchReturnsNoContent
                ? ScimResults.NoContent()
                : ScimResults.Json(200, Project(ScimUserMapper.ToJson(updated, BaseUrl(http)), http.Request));
        });

        scim.MapDelete("/Users/{id}", async (HttpContext http, string id, ScimUserService users) =>
        {
            await users.DeleteAsync(ParseId(id, "User"), ClientOf(http), http.RequestAborted);
            return ScimResults.NoContent();
        });
    }

    // ---- groups -------------------------------------------------------------------------------

    private static void MapGroups(RouteGroupBuilder scim)
    {
        scim.MapGet("/Groups", async (HttpContext http, ScimGroupService groups, IOptions<ScimOptions> opt) =>
        {
            var (start, count) = Paging(http.Request, opt.Value);
            var withMembers = !ExcludesMembers(http.Request);
            var page = await groups.ListAsync(http.Request.Query["filter"].FirstOrDefault(), start, count, withMembers, http.RequestAborted);
            var baseUrl = BaseUrl(http);
            return ScimResults.Json(200, ListResponse(page.Total, start, page.Items.Select(g => ScimGroupMapper.ToJson(g, baseUrl, withMembers)), http.Request));
        });

        scim.MapPost("/Groups", async (HttpContext http, ScimGroupService groups) =>
        {
            var created = await groups.CreateAsync(await ScimResults.ReadBodyAsync(http.Request), ClientOf(http), http.RequestAborted);
            var json = ScimGroupMapper.ToJson(created, BaseUrl(http), !ExcludesMembers(http.Request));
            return ScimResults.Json(201, Project(json, http.Request), json["meta"]!["location"]!.GetValue<string>());
        });

        scim.MapGet("/Groups/{id}", async (HttpContext http, string id, ScimGroupService groups) =>
        {
            var group = Guid.TryParse(id, out var guid) ? await groups.FindAsync(guid, http.RequestAborted) : null;
            return group is null
                ? ScimResults.Error(404, null, $"Group {id} not found")
                : ScimResults.Json(200, Project(ScimGroupMapper.ToJson(group, BaseUrl(http), !ExcludesMembers(http.Request)), http.Request));
        });

        scim.MapPut("/Groups/{id}", async (HttpContext http, string id, ScimGroupService groups) =>
        {
            var updated = await groups.ReplaceAsync(ParseId(id, "Group"), await ScimResults.ReadBodyAsync(http.Request), ClientOf(http), http.RequestAborted);
            return ScimResults.Json(200, Project(ScimGroupMapper.ToJson(updated, BaseUrl(http), !ExcludesMembers(http.Request)), http.Request));
        });

        scim.MapMethods("/Groups/{id}", ["PATCH"], async (HttpContext http, string id, ScimGroupService groups, IOptions<ScimOptions> opt) =>
        {
            var updated = await groups.PatchAsync(ParseId(id, "Group"), await ScimResults.ReadBodyAsync(http.Request), ClientOf(http), http.RequestAborted);
            return opt.Value.PatchReturnsNoContent
                ? ScimResults.NoContent()
                : ScimResults.Json(200, Project(ScimGroupMapper.ToJson(updated, BaseUrl(http), !ExcludesMembers(http.Request)), http.Request));
        });

        scim.MapDelete("/Groups/{id}", async (HttpContext http, string id, ScimGroupService groups) =>
        {
            await groups.DeleteAsync(ParseId(id, "Group"), ClientOf(http), http.RequestAborted);
            return ScimResults.NoContent();
        });
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static async ValueTask<object?> ErrorFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (ScimException ex)
        {
            return ScimResults.Error(ex);
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Scim")
                .LogError(ex, "Unhandled SCIM error");
            return ScimResults.Error(500, null, "Internal server error");
        }
    }

    private static Guid ParseId(string id, string type) =>
        Guid.TryParse(id, out var guid) ? guid : throw ScimException.NotFound($"{type} {id} not found");

    private static (int Start, int Count) Paging(HttpRequest request, ScimOptions options)
    {
        var start = ReadInt(request, "startIndex") ?? 1;
        var count = ReadInt(request, "count") ?? options.DefaultCount;
        return (Math.Max(1, start), Math.Clamp(count, 0, options.MaxCount));
    }

    private static int? ReadInt(HttpRequest request, string name)
    {
        var raw = request.Query[name].FirstOrDefault();
        if (raw is null) return null;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw ScimException.BadRequest("invalidValue", $"'{name}' must be an integer");
    }

    private static JsonObject ListResponse(int total, int start, IEnumerable<JsonObject> resources, HttpRequest request)
    {
        var projected = resources.Select(r => (JsonNode)Project(r, request)).ToArray();
        return new JsonObject
        {
            ["schemas"] = new JsonArray(ScimSchemas.ListResponse),
            ["totalResults"] = total,
            ["startIndex"] = start,
            ["itemsPerPage"] = projected.Length,
            ["Resources"] = new JsonArray(projected),
        };
    }

    private static bool ExcludesMembers(HttpRequest request) =>
        Names(request, "excludedAttributes").Contains("members");

    private static HashSet<string> Names(HttpRequest request, string parameter) =>
        (request.Query[parameter].FirstOrDefault() ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.Split('.')[0].ToLowerInvariant()).ToHashSet();

    /// <summary>Applies <c>attributes</c> / <c>excludedAttributes</c> on top-level attribute names (id, schemas, meta always kept).</summary>
    private static JsonObject Project(JsonObject resource, HttpRequest request)
    {
        var include = Names(request, "attributes");
        var exclude = Names(request, "excludedAttributes");
        if (include.Count == 0 && exclude.Count == 0) return resource;

        var always = new HashSet<string> { "schemas", "id", "meta" };
        foreach (var key in resource.Select(p => p.Key).ToList())
        {
            var lower = key.ToLowerInvariant();
            if (always.Contains(lower)) continue;
            if (exclude.Contains(lower) || include.Count > 0 && !include.Contains(lower)) resource.Remove(key);
        }
        return resource;
    }

    private static string BaseUrl(HttpContext http)
    {
        var configured = http.RequestServices.GetRequiredService<IOptions<ProviderOptions>>().Value.PublicBaseUrl;
        return string.IsNullOrWhiteSpace(configured)
            ? $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}"
            : configured.TrimEnd('/');
    }

    private static string ClientOf(HttpContext http) =>
        http.User.FindFirst("client_id")?.Value ?? http.User.Identity?.Name ?? "unknown";
}
