using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Tcp.Api.Auth;
using Tcp.Api.Configuration;
using Tcp.Api.Security;
using Tcp.Domain.Tasks;
using Tcp.Infrastructure.Tasks;

namespace Tcp.Api.Endpoints.Spi;

/// <summary>
/// SAP Task Center Service Provider Interface v2, MVP scope (spec 004): E1-E8 on both base paths; everything else of
/// <c>TaskProviderV2.json</c> answers <c>501 tcp.spi.notImplemented</c>.
/// </summary>
public static partial class SpiEndpoints
{
    public const int MaxPageSize = 1000;
    public const int DefaultPageSize = 100;

    [GeneratedRegex(@"^\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])T(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d\.\d{3}Z$")]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"^[a-z]{2}-[A-Z]{2}$")]
    private static partial Regex LanguageTag();

    public static IEndpointRouteBuilder MapSpi(this IEndpointRouteBuilder app)
    {
        foreach (var basePath in SpiPaths.Prefixes)
        {
            var spi = app.MapGroup(basePath).RequireAuthorization(Policies.SpiAny);
            spi.AddEndpointFilter(ErrorFilter);

            spi.MapGet("/capabilities", Capabilities);
            spi.MapGet("/taskDefinitions", ListDefinitions);
            spi.MapGet("/taskDefinitions/{urn}", GetDefinition);
            spi.MapGet("/tasks", PullTasks);
            spi.MapGet("/tasks/{urn}", GetTask);

            // user-context endpoints: additionally require a spi.user token (tech token => 403 userContextRequired)
            spi.MapGet("/tasks/{urn}/description", GetDescription).RequireAuthorization(Policies.SpiUser);
            spi.MapPost("/tasks/{urn}/response", Respond).RequireAuthorization(Policies.SpiUser);
            spi.MapPost("/tasks/{urn}/action", Act).RequireAuthorization(Policies.SpiUser);

            spi.MapMethods("/{**rest}", ["GET", "POST", "PUT", "PATCH", "DELETE"], NotImplemented);
        }
        return app;
    }

    // ---- E1 capabilities ----------------------------------------------------------------------

    private static IResult Capabilities() => Results.Json(new JsonObject
    {
        ["value"] = new JsonArray(
            Capability("tasks.pull", "true"),
            Capability("taskDefinitions.pull", "true"),
            Capability("tasks.push", "false"),
            Capability("substitutions", "false"),
            Capability("user.existence", "false"),
            Capability("global.operations", "false")),
    });

    private static JsonNode Capability(string name, string value) => new JsonObject { ["name"] = name, ["value"] = value };

    // ---- E2/E3 definitions --------------------------------------------------------------------

    private static async Task<IResult> ListDefinitions(HttpContext http, DefinitionRepository definitions, SpiTaskService spi)
    {
        var languages = RequireLanguages(http.Request);
        var top = ReadInt(http.Request, "$top", DefaultPageSize, 1, MaxPageSize);
        var skip = ReadInt(http.Request, "$skip", 0, 0, int.MaxValue);

        var page = await definitions.ListAsync(skip, top, http.RequestAborted);
        var ctx = spi.Context(http, languages);
        return Results.Json(new JsonObject { ["value"] = new JsonArray(page.Select(d => (JsonNode)SpiMapper.Definition(d, ctx)).ToArray()) });
    }

    private static async Task<IResult> GetDefinition(HttpContext http, string urn, DefinitionRepository definitions, SpiTaskService spi)
    {
        var languages = RequireLanguages(http.Request);
        var parsed = ParseUrn(urn, UrnKind.TaskDefinition);
        var definition = parsed is null ? null : await definitions.FindAsync(parsed, http.RequestAborted);
        if (definition is null) throw new TaskRuleViolation(SpiCodes.TaskDefinitionNotFound);
        return Results.Json(SpiMapper.Definition(definition, spi.Context(http, languages)));
    }

    // ---- E4/E5 tasks --------------------------------------------------------------------------

    private static async Task<IResult> PullTasks(HttpContext http, TaskRepository tasks, SpiTaskService spi)
    {
        var languages = RequireLanguages(http.Request);
        var top = ReadInt(http.Request, "$top", DefaultPageSize, 1, MaxPageSize);

        DateTime? after = null;
        var rawAfter = http.Request.Query["modifiedAfter"].FirstOrDefault();
        if (rawAfter is not null)
        {
            if (!Timestamp().IsMatch(rawAfter) ||
                !DateTime.TryParseExact(rawAfter, SpiMapper.TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                throw new TaskRuleViolation(SpiCodes.InvalidParameter, "modifiedAfter");
            after = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        var lastId = http.Request.Query["lastId"].FirstOrDefault();
        if (lastId is not null && (after is null || lastId.Length > Urn.MaxLength))
            throw new TaskRuleViolation(SpiCodes.InvalidParameter, "lastId");

        var page = await tasks.PullAsync(new PullQuery(after, lastId, top), http.RequestAborted);
        return Results.Json(new JsonObject { ["value"] = await spi.MapAsync(page, spi.Context(http, languages), http.RequestAborted) });
    }

    private static async Task<IResult> GetTask(HttpContext http, string urn, TaskRepository tasks, SpiTaskService spi)
    {
        var languages = RequireLanguages(http.Request);
        var parsed = ParseUrn(urn, UrnKind.Task);
        var task = parsed is null ? null : await tasks.FindAsync(parsed, http.RequestAborted);
        if (task is null) throw new TaskRuleViolation(SpiCodes.TaskNotFound);
        return Results.Json(await spi.MapOneAsync(task, spi.Context(http, languages), http.RequestAborted));
    }

    // ---- E6 description -----------------------------------------------------------------------

    private static async Task<IResult> GetDescription(
        HttpContext http, string urn, TaskRepository tasks, OperationService operations, ICurrentUser user, IOptions<ProviderOptions> provider)
    {
        var parsed = ParseUrn(urn, UrnKind.Task);
        var task = parsed is null ? null : await tasks.FindAsync(parsed, http.RequestAborted);
        if (task is null) throw new TaskRuleViolation(SpiCodes.TaskNotFound);

        await operations.CheckEntitlementAsync(task, user.GlobalUserId!, http.RequestAborted);

        var descriptions = TaskDescriptionSelector.Parse(task.DescriptionJson);
        var preferred = AcceptedLanguages(http.Request);
        var chosen = TaskDescriptionSelector.Select(descriptions, preferred, provider.Value.DefaultLanguage)
            ?? throw new TaskRuleViolation(SpiCodes.DescriptionNotFound);

        // TaskProviderV2.json: "Returns the plain text task description" (text/plain; charset=utf-8). Descriptions are
        // authored as sanitised HTML for the provider's own UI, so convert for the SPI.
        var plain = chosen.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
            ? HtmlText.ToPlainText(chosen.Body)
            : chosen.Body;
        http.Response.Headers.ContentLanguage = chosen.LanguageCode;
        return Results.Content(plain, "text/plain; charset=utf-8", Encoding.UTF8);
    }

    // ---- E7/E8 operations ---------------------------------------------------------------------

    private static async Task<IResult> Respond(HttpContext http, string urn, OperationService operations, SpiTaskService spi, ICurrentUser user)
    {
        var languages = RequireLanguages(http.Request);
        var request = await ReadOperationAsync(http.Request);
        var updated = await operations.ExecuteAsync(OperationKind.Response, NormalizeUrn(urn), user.GlobalUserId!, request, http.RequestAborted);
        return updated is null
            ? Results.StatusCode(StatusCodes.Status202Accepted)
            : Results.Json(await spi.MapOneAsync(updated, spi.Context(http, languages), http.RequestAborted));
    }

    private static async Task<IResult> Act(HttpContext http, string urn, OperationService operations, SpiTaskService spi, ICurrentUser user)
    {
        var languages = RequireLanguages(http.Request);
        var request = await ReadOperationAsync(http.Request);
        var updated = await operations.ExecuteAsync(OperationKind.Action, NormalizeUrn(urn), user.GlobalUserId!, request, http.RequestAborted);
        return Results.Json(await spi.MapOneAsync(updated!, spi.Context(http, languages), http.RequestAborted));
    }

    private static async Task<OperationRequest> ReadOperationAsync(HttpRequest request)
    {
        JsonObject? body;
        try
        {
            body = await request.ReadFromJsonAsync<JsonObject>(request.HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException or InvalidOperationException)
        {
            throw new TaskRuleViolation(SpiCodes.InvalidParameter, "body");
        }
        if (body is null) throw new TaskRuleViolation(SpiCodes.InvalidParameter, "body");

        var code = OptionalString(body, "code", "code");
        if (string.IsNullOrWhiteSpace(code)) throw new TaskRuleViolation(SpiCodes.InvalidParameter, "code");
        return new OperationRequest(code, OptionalString(body, "comment", "comment"), OptionalString(body, "reasonCode", "reasonCode"));
    }

    private static string? OptionalString(JsonObject body, string property, string target)
    {
        if (!body.TryGetPropertyValue(property, out var node) || node is null) return null;
        return node is JsonValue v && v.TryGetValue<string>(out var s)
            ? s
            : throw new TaskRuleViolation(SpiCodes.InvalidParameter, target);
    }

    // ---- 501 for everything else ----------------------------------------------------------------

    private static IResult NotImplemented(HttpContext http) => SpiErrors.Result(http, SpiCodes.NotImplemented);

    // ---- helpers --------------------------------------------------------------------------------

    private static async ValueTask<object?> ErrorFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (TaskRuleViolation violation)
        {
            return SpiErrors.Result(context.HttpContext, violation.Code, violation.Target);
        }
    }

    /// <summary>Raw or percent-encoded URN (a second decoding pass handles clients that double-encode).</summary>
    private static string NormalizeUrn(string urn) => urn.Contains('%', StringComparison.Ordinal) ? Uri.UnescapeDataString(urn) : urn;

    private static string? ParseUrn(string urn, UrnKind kind)
    {
        var normalized = NormalizeUrn(urn);
        return Urn.TryParse(normalized, out var parsed) && parsed.Kind == kind ? normalized : null;
    }

    internal static IReadOnlyList<string> RequireLanguages(HttpRequest request)
    {
        var raw = string.Join(',', request.Query["languages"].ToArray());
        var languages = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToList();
        if (languages.Count is 0 or > 10 || languages.Any(l => !LanguageTag().IsMatch(l)))
            throw new TaskRuleViolation(SpiCodes.InvalidParameter, "languages");
        return languages;
    }

    private static int ReadInt(HttpRequest request, string name, int fallback, int min, int max)
    {
        var raw = request.Query[name].FirstOrDefault();
        if (raw is null) return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new TaskRuleViolation(SpiCodes.InvalidParameter, name);
        return value;
    }

    private static IEnumerable<string> AcceptedLanguages(HttpRequest request)
    {
        try
        {
            return request.GetTypedHeaders().AcceptLanguage
                .OrderByDescending(a => a.Quality ?? 1.0)
                .Select(a => a.Value.Value ?? string.Empty).ToList();
        }
        catch (FormatException)
        {
            return [];
        }
    }
}
