using System.Text.Json.Nodes;
using Tcp.Api.Endpoints.Spi;
using Tcp.Domain.Tasks;

namespace Tcp.Api.Endpoints.Admin;

public sealed record RecipientsDto(List<string>? Users, List<string>? Groups);

public sealed record DescriptionDto(string? ContentType, string? Body);

public sealed record CreateTaskRequest(
    string? DefinitionLocalId,
    RecipientsDto? Recipients,
    Dictionary<string, string>? Subject,
    string? Priority,
    DateTime? DueAt,
    Dictionary<string, string?>? CustomAttributes,
    Dictionary<string, DescriptionDto>? Description,
    string? CreatedBy);

public sealed record CompleteTaskRequest(string? UserId, string? Code, string? Comment, string? ReasonCode);

public sealed record GenerateTasksRequest(
    int Count,
    string? DefinitionLocalId,
    RecipientsDto? Recipients,
    bool SameTimestamp,
    int? Seed);

public sealed record AdminError(string Field, string Message);

/// <summary>An admin API failure that is reported as RFC 9457 problem details with a list of offending fields.</summary>
public sealed class AdminException(int status, string title, IReadOnlyList<AdminError> errors) : Exception(title)
{
    public int Status { get; } = status;
    public IReadOnlyList<AdminError> Errors { get; } = errors;

    public static AdminException Invalid(params AdminError[] errors) =>
        new(StatusCodes.Status400BadRequest, "The request is not valid", errors);

    public static AdminException Invalid(IReadOnlyList<AdminError> errors) =>
        new(StatusCodes.Status400BadRequest, "The request is not valid", errors);

    public static AdminException NotFound(string what) =>
        new(StatusCodes.Status404NotFound, $"{what} was not found", []);

    public static AdminException FromRule(TaskRuleViolation violation) =>
        new(SpiErrors.StatusFor(violation.Code), violation.Code,
            [new AdminError(violation.Target ?? "task", SpiMessages.Get(violation.Code, SpiMessages.English, violation.Target ?? string.Empty))]);
}

/// <summary>What a task creation or update needs after validation.</summary>
public sealed record ResolvedRecipients(IReadOnlyList<string> UserIds, IReadOnlyList<string> Groups);

public static class AdminJson
{
    public static string? String(JsonObject body, string name) =>
        body.TryGetPropertyValue(name, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
