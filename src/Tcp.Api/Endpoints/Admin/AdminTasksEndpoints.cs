using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Tcp.Api.Endpoints.Spi;
using Tcp.Domain.Tasks;
using Tcp.Infrastructure.Persistence;
using Tcp.Infrastructure.Tasks;

namespace Tcp.Api.Endpoints.Admin;

public static class AdminTasksEndpoints
{
    public static readonly string[] DefaultLanguages = ["en-US", "de-DE"];

    public static RouteGroupBuilder MapAdminTasks(this RouteGroupBuilder admin)
    {
        // POST /admin/api/tasks  -> 201 + SPI representation
        admin.MapPost("/tasks", async (HttpContext http, CreateTaskRequest request, AdminTaskService service, SpiTaskService spi) =>
        {
            var task = await service.CreateAsync(request, http.RequestAborted);
            var json = await spi.MapOneAsync(task, spi.Context(http, Languages(http)), http.RequestAborted);
            return Results.Created($"/admin/api/tasks/{Uri.EscapeDataString(task.Urn)}", json);
        });

        admin.MapPatch("/tasks/{urn}", async (HttpContext http, string urn, JsonObject patch, AdminTaskService service, SpiTaskService spi) =>
        {
            var task = await service.UpdateAsync(Urn(urn), patch, http.RequestAborted);
            return Results.Ok(await spi.MapOneAsync(task, spi.Context(http, Languages(http)), http.RequestAborted));
        });

        admin.MapPost("/tasks/{urn}/complete", async (HttpContext http, string urn, CompleteTaskRequest request, AdminTaskService service, SpiTaskService spi) =>
            Results.Ok(await spi.MapOneAsync(await service.CompleteAsync(Urn(urn), request, http.RequestAborted),
                spi.Context(http, Languages(http)), http.RequestAborted)));

        admin.MapPost("/tasks/{urn}/cancel", async (HttpContext http, string urn, AdminTaskService service, SpiTaskService spi) =>
            Results.Ok(await spi.MapOneAsync(await service.CancelAsync(Urn(urn), http.RequestAborted), spi.Context(http, Languages(http)), http.RequestAborted)));

        admin.MapPost("/tasks/{urn}/deactivate", async (HttpContext http, string urn, AdminTaskService service, SpiTaskService spi) =>
            Results.Ok(await spi.MapOneAsync(await service.DeactivateAsync(Urn(urn), http.RequestAborted), spi.Context(http, Languages(http)), http.RequestAborted)));

        admin.MapPost("/tasks/{urn}/reactivate", async (HttpContext http, string urn, AdminTaskService service, SpiTaskService spi) =>
            Results.Ok(await spi.MapOneAsync(await service.ReactivateAsync(Urn(urn), http.RequestAborted), spi.Context(http, Languages(http)), http.RequestAborted)));

        // GDPR: tasks are never hard-deleted (TC-GDPR), so there is deliberately no DELETE endpoint; say so explicitly.
        admin.MapDelete("/tasks/{urn}", () => Results.Problem(
            title: "Tasks are never deleted", detail: "Cancel the task instead; Task Center needs the CANCELED tombstone.",
            statusCode: StatusCodes.Status405MethodNotAllowed));

        admin.MapPost("/tasks/generate", async (HttpContext http, GenerateTasksRequest request, AdminTaskService service) =>
            Results.Ok(await service.GenerateAsync(request, http.RequestAborted)));

        // GET /admin/api/definitions: what the "New task" form needs (attribute codes/types, responses, actions)
        admin.MapGet("/definitions", async (HttpContext http, DefinitionRepository definitions) =>
        {
            var all = await definitions.ListAsync(0, 100, http.RequestAborted);
            return Results.Ok(new JsonArray(all.Select(d => (JsonNode)new JsonObject
            {
                ["localId"] = d.LocalId,
                ["urn"] = d.Urn,
                ["name"] = LocalizedTextSelector.Select(d.Name, ["en-US"], "en-US").FirstOrDefault()?.Text ?? d.LocalId,
                ["attributes"] = new JsonArray(d.CustomAttributes.OrderByDescending(a => a.Rank).Select(a => (JsonNode)new JsonObject
                {
                    ["code"] = a.Code, ["type"] = a.Type,
                    ["name"] = LocalizedTextSelector.Select(a.Name, ["en-US"], "en-US").FirstOrDefault()?.Text ?? a.Code,
                }).ToArray()),
                ["responses"] = new JsonArray(d.Responses.Select(r => (JsonNode)r.Code).ToArray()),
                ["actions"] = new JsonArray(d.Actions.Select(a => (JsonNode)a.Code).ToArray()),
            }).ToArray()));
        });

        admin.MapGet("/tasks", ListAsync);
        admin.MapGet("/tasks/{urn}", DetailAsync);
        return admin;
    }

    private static string[] Languages(HttpContext http)
    {
        var raw = http.Request.Query["languages"].FirstOrDefault();
        var parsed = raw?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        return parsed.Length > 0 ? parsed : DefaultLanguages;
    }

    private static string Urn(string urn) =>
        SpiEndpoints.ParseUrn(urn, UrnKind.Task) ?? throw AdminException.NotFound("The task");

    // GET /admin/api/tasks?status&definition&user&search&top&skip
    private static async Task<IResult> ListAsync(HttpContext http, TcpDbContext db, DefinitionRepository definitions)
    {
        var q = http.Request.Query;
        IQueryable<TaskInstance> query = db.Tasks.AsNoTracking();

        if (q["status"].FirstOrDefault() is { Length: > 0 } status)
        {
            if (!TaskStatuses.All.Contains(status))
                throw AdminException.Invalid(new AdminError("status", $"must be one of {string.Join(", ", TaskStatuses.All)}"));
            query = query.Where(t => t.Status == status);
        }

        if (q["definition"].FirstOrDefault() is { Length: > 0 } definition)
        {
            var found = definition.StartsWith("urn:", StringComparison.Ordinal) ? await definitions.FindAsync(definition, http.RequestAborted)
                                                                                  : await definitions.FindByLocalIdAsync(definition, http.RequestAborted);
            var definitionUrn = found?.Urn ?? string.Empty;
            query = query.Where(t => t.DefinitionUrn == definitionUrn);
        }

        if (q["user"].FirstOrDefault() is { Length: > 0 } user)
            query = query.Where(t => t.Processor == user || t.CreatedBy == user || t.RecipientUsers.Any(r => r.GlobalUserId == user));

        if (q["search"].FirstOrDefault() is { Length: > 0 } search)
        {
            var pattern = SqlLike.Contains(search);
            query = query.Where(t => EF.Functions.Like(t.SubjectJson, pattern, SqlLike.EscapeCharacter) ||
                                     EF.Functions.Like(EF.Functions.Collate(t.LocalId, "SQL_Latin1_General_CP1_CI_AS"), pattern, SqlLike.EscapeCharacter));
        }

        var top = ReadInt(q["top"].FirstOrDefault(), "top", 50, 1, 500);
        var skip = ReadInt(q["skip"].FirstOrDefault(), "skip", 0, 0, int.MaxValue);

        var total = await query.CountAsync(http.RequestAborted);
        var page = await query.OrderByDescending(t => t.ModifiedAt).ThenBy(t => t.Urn).Skip(skip).Take(top)
            .Select(t => new
            {
                t.Urn, t.LocalId, t.DefinitionUrn, t.Status, t.Priority, t.SubjectJson, t.Processor, t.CreatedAt, t.ModifiedAt, t.DueAt,
                Users = t.RecipientUsers.Count(), Groups = t.RecipientGroups.Count(),
            }).ToListAsync(http.RequestAborted);

        var defaultLanguage = http.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<Configuration.ProviderOptions>>().Value.DefaultLanguage;
        return Results.Ok(new JsonObject
        {
            ["total"] = total, ["top"] = top, ["skip"] = skip,
            ["items"] = new JsonArray(page.Select(t => (JsonNode)new JsonObject
            {
                ["urn"] = t.Urn,
                ["localId"] = t.LocalId,
                ["definition"] = t.DefinitionUrn[(t.DefinitionUrn.LastIndexOf(':') + 1)..],
                ["status"] = t.Status,
                ["priority"] = t.Priority,
                ["subject"] = LocalizedTextSelector.Select(TaskDefinition.ParseTexts(JsonNode.Parse(t.SubjectJson)), [defaultLanguage], defaultLanguage)
                    .FirstOrDefault()?.Text,
                ["processor"] = t.Processor,
                ["createdAt"] = SpiMapper.Format(t.CreatedAt),
                ["modifiedAt"] = SpiMapper.Format(t.ModifiedAt),
                ["dueAt"] = t.DueAt is { } due ? SpiMapper.Format(due) : null,
                ["recipientUsers"] = t.Users,
                ["recipientGroups"] = t.Groups,
            }).ToArray()),
        });
    }

    // GET /admin/api/tasks/{urn}: everything the UI and the "Open in App" page show
    private static async Task<IResult> DetailAsync(HttpContext http, string urn, TaskRepository tasks, SpiTaskService spi, TcpDbContext db)
    {
        var task = await tasks.FindAsync(Urn(urn), http.RequestAborted) ?? throw AdminException.NotFound("The task");
        var detail = await TaskDetail.LoadAsync(task, db, http.RequestAborted);
        var spiJson = await spi.MapOneAsync(task, spi.Context(http, Languages(http)), http.RequestAborted);
        return Results.Ok(detail.ToJson(spiJson));
    }

    private static int ReadInt(string? raw, string name, int fallback, int min, int max)
    {
        if (raw is null) return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw AdminException.Invalid(new AdminError(name, $"must be an integer between {min} and {max}"));
        return value;
    }
}

/// <summary>Task plus the people and history around it, shared by the admin API and the app page.</summary>
public sealed record TaskDetail(
    TaskInstance Task,
    IReadOnlyList<TaskDescription> Descriptions,
    IReadOnlyList<Domain.Identity.ScimUser> Users,
    IReadOnlyList<OperationLogEntry> Operations)
{
    public static async Task<TaskDetail> LoadAsync(TaskInstance task, TcpDbContext db, CancellationToken ct)
    {
        var ids = task.RecipientUsers.Select(r => r.GlobalUserId).Concat(new[] { task.Processor, task.CreatedBy, task.CompletedBy }.OfType<string>())
            .Distinct(StringComparer.Ordinal).ToList();
        var users = await db.ScimUsers.AsNoTracking().Where(u => !u.IsDeleted && u.GlobalUserId != null && ids.Contains(u.GlobalUserId)).ToListAsync(ct);
        var operations = await db.OperationLog.AsNoTracking().Where(o => o.TaskUrn == task.Urn).OrderBy(o => o.Id).ToListAsync(ct);
        return new TaskDetail(task, TaskDescriptionSelector.Parse(task.DescriptionJson), users, operations);
    }

    public JsonObject ToJson(JsonObject spi) => new()
    {
        ["task"] = spi,
        ["descriptions"] = new JsonArray(Descriptions.Select(d => (JsonNode)new JsonObject
            { ["languageCode"] = d.LanguageCode, ["contentType"] = d.ContentType, ["body"] = d.Body }).ToArray()),
        ["users"] = new JsonArray(Users.Select(u => (JsonNode)new JsonObject
        {
            ["globalUserId"] = u.GlobalUserId, ["userName"] = u.UserName, ["displayName"] = u.DisplayName, ["email"] = u.PrimaryEmail, ["active"] = u.Active,
        }).ToArray()),
        ["groups"] = new JsonArray(Task.RecipientGroups.Select(g => (JsonNode)g.GroupName).ToArray()),
        ["operations"] = new JsonArray(Operations.Select(OperationJson).ToArray()),
        ["errors"] = new JsonArray(Task.OperationErrors.Select(e => (JsonNode)new JsonObject
            { ["executedAt"] = SpiMapper.Format(e.ExecutedAt), ["code"] = e.Code, ["message"] = e.Message, ["executedBy"] = e.ExecutedBy }).ToArray()),
    };

    public static JsonNode OperationJson(OperationLogEntry o) => new JsonObject
    {
        ["id"] = o.Id, ["taskUrn"] = o.TaskUrn, ["kind"] = o.Kind, ["code"] = o.Code, ["comment"] = o.Comment, ["reasonCode"] = o.ReasonCode,
        ["userId"] = o.UserId, ["at"] = SpiMapper.Format(o.At), ["outcome"] = o.Outcome, ["errorCode"] = o.ErrorCode,
    };
}
