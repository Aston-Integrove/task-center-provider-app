using System.Globalization;
using System.Text.Json.Nodes;
using Tcp.Domain.Tasks;

namespace Tcp.Api.Endpoints.Spi;

/// <summary>What the mapper needs to know about this provider instance and the current request.</summary>
public sealed record SpiContext(
    string ApplicationId,
    string ApplicationInstanceId,
    string TenantId,
    string BaseUrl,
    string DefaultLanguage,
    IReadOnlyList<string> Languages);

/// <summary>
/// Domain to SAP wire format (TaskProviderV2.json). Built as <see cref="JsonObject"/>s so the exact shape is under
/// control (FR-SPI-06): camelCase, millisecond UTC timestamps, explicit <c>null</c> where SAP examples use it
/// (<c>processor</c>, <c>dueAt</c>, <c>completedAt</c>, <c>validResponseCodes</c> of open tasks - all declared nullable
/// in the SAP schema), and no noise for the other optional properties. Never emit <c>null</c> for a property the
/// contract does not mark nullable (e.g. <c>recipientGroups</c>): the contract tests enforce this.
/// </summary>
public static class SpiMapper
{
    public const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public static string Format(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString(TimestampFormat, CultureInfo.InvariantCulture);

    public static string UiLink(string baseUrl, string taskUrn) =>
        $"{baseUrl.TrimEnd('/')}/app/tasks/{Uri.EscapeDataString(taskUrn)}";

    // ---- definitions --------------------------------------------------------------------------

    public static JsonObject Definition(TaskDefinition d, SpiContext ctx)
    {
        var result = new JsonObject
        {
            ["urn"] = d.Urn,
            ["applicationId"] = ctx.ApplicationId,
            ["applicationInstanceId"] = ctx.ApplicationInstanceId,
            ["tenantId"] = ctx.TenantId,
            ["localId"] = d.LocalId,
            ["name"] = Texts(d.Name, ctx),
            ["possibleResponses"] = new JsonArray(d.Responses.Select(o => (JsonNode)Operation(o, ctx)).ToArray()),
            ["possibleActions"] = new JsonArray(d.Actions.Select(o => (JsonNode)Operation(o, ctx)).ToArray()),
            ["customAttributes"] = new JsonArray(d.CustomAttributes.OrderByDescending(a => a.Rank).ThenBy(a => a.Code, StringComparer.Ordinal)
                .Select(a => (JsonNode)AttributeDefinition(a, ctx)).ToArray()),
            ["capabilities"] = d.Capabilities.DeepClone(),
        };
        if (d.TaskDetailsSettings is not null) result["taskDetailsSettings"] = d.TaskDetailsSettings.DeepClone();
        return result;
    }

    private static JsonObject Operation(OperationDefinition o, SpiContext ctx)
    {
        var result = new JsonObject
        {
            ["code"] = o.Code,
            ["name"] = Texts(o.Name, ctx),
            ["nature"] = o.Nature,
            ["commentRequired"] = o.CommentRequired,
            ["reasonRequired"] = o.ReasonRequired,
            ["possibleReasons"] = new JsonArray(o.PossibleReasons.Select(r => (JsonNode)new JsonObject
            {
                ["code"] = r.Code,
                ["name"] = Texts(r.Name, ctx),
            }).ToArray()),
        };
        if (o.Capabilities is not null) result["capabilities"] = o.Capabilities.DeepClone();
        return result;
    }

    private static JsonObject AttributeDefinition(CustomAttributeDefinition a, SpiContext ctx)
    {
        var result = new JsonObject { ["code"] = a.Code, ["type"] = a.Type };
        if (a.Format is not null) result["format"] = a.Format;
        result["name"] = Texts(a.Name, ctx);
        result["rank"] = a.Rank;
        return result;
    }

    private static JsonArray Texts(IReadOnlyList<LocalizedText> texts, SpiContext ctx) =>
        TextsOf(LocalizedTextSelector.Select(texts, ctx.Languages, ctx.DefaultLanguage));

    private static JsonArray TextsOf(IReadOnlyList<SelectedText> selected) =>
        new(selected.Select(t => (JsonNode)new JsonObject
        {
            ["languageCode"] = t.LanguageCode,
            ["text"] = t.Text,
            ["isDefault"] = t.IsDefault,
        }).ToArray());

    // ---- tasks --------------------------------------------------------------------------------

    /// <param name="activeUserIds">Of the task's users, those that are active SCIM users (US-004-4.2).</param>
    /// <param name="onInvalidAttribute">Called for each stored custom attribute that is dropped (logged by the caller).</param>
    public static JsonObject Task(
        TaskInstance t,
        TaskDefinition? definition,
        SpiContext ctx,
        IReadOnlySet<string> activeUserIds,
        Action<string, string>? onInvalidAttribute = null)
    {
        var subject = JsonNode.Parse(t.SubjectJson) is JsonArray array ? TaskDefinition.ParseTexts(array) : [];

        var result = new JsonObject
        {
            ["urn"] = t.Urn,
            ["applicationId"] = ctx.ApplicationId,
            ["applicationInstanceId"] = ctx.ApplicationInstanceId,
            ["tenantId"] = ctx.TenantId,
            ["localId"] = t.LocalId,
            ["definitionId"] = t.DefinitionUrn,
            ["status"] = t.Status,
            ["priority"] = t.Priority,
            ["subject"] = Texts(subject, ctx),
            ["createdAt"] = Format(t.CreatedAt),
        };
        if (t.CreatedBy is not null) result["createdBy"] = t.CreatedBy;
        result["modifiedAt"] = Format(t.ModifiedAt);
        if (t.ModifiedBy is not null) result["modifiedBy"] = t.ModifiedBy;
        result["processor"] = t.Processor;
        result["dueAt"] = t.DueAt is { } due ? Format(due) : null;
        result["completedAt"] = t.CompletedAt is { } done ? Format(done) : null;
        if (t.CompletedBy is not null) result["completedBy"] = t.CompletedBy;
        result["uiLink"] = UiLink(ctx.BaseUrl, t.Urn);
        result["customAttributes"] = CustomAttributes(t, definition, onInvalidAttribute);

        var validResponses = definition is null ? (t.IsFinal ? [] : null) : OperationRules.ValidResponseCodes(t, definition);
        result["validResponseCodes"] = validResponses is null ? null : new JsonArray(validResponses.Select(c => (JsonNode)c).ToArray());
        var validActions = definition is null ? [] : OperationRules.ValidActionCodes(t, definition);
        result["validActionCodes"] = new JsonArray(validActions.Select(c => (JsonNode)c).ToArray());

        var users = t.RecipientUsers.Select(r => r.GlobalUserId).Where(activeUserIds.Contains).ToHashSet(StringComparer.Ordinal);
        if (t.Processor is not null) users.Add(t.Processor); // the processor is always part of recipientUsers
        result["recipientUsers"] = new JsonArray(users.Order(StringComparer.Ordinal).Select(u => (JsonNode)u).ToArray());

        // TaskProviderV2.json declares recipientGroups as a non-nullable array, so "no groups" is [] (never null).
        var groups = t.RecipientGroups.Select(g => g.GroupName).Order(StringComparer.Ordinal).ToList();
        result["recipientGroups"] = new JsonArray(groups.Select(g => (JsonNode)g).ToArray());

        if (t.OperationErrors.Count > 0)
        {
            // latest per executing user
            var latest = t.OperationErrors.GroupBy(e => e.ExecutedBy).Select(g => g.OrderByDescending(e => e.Id).First())
                .OrderBy(e => e.ExecutedAt).ThenBy(e => e.Id);
            result["operationErrors"] = new JsonArray(latest.Select(e => (JsonNode)new JsonObject
            {
                ["executedAt"] = Format(e.ExecutedAt),
                ["code"] = e.Code,
                ["message"] = e.Message,
                ["executedBy"] = e.ExecutedBy,
            }).ToArray());
        }
        return result;
    }

    private static JsonArray CustomAttributes(TaskInstance t, TaskDefinition? definition, Action<string, string>? onInvalid)
    {
        var result = new JsonArray();
        if (definition is null) return result;

        foreach (var def in definition.CustomAttributes.OrderByDescending(a => a.Rank).ThenBy(a => a.Code, StringComparer.Ordinal))
        {
            var stored = t.CustomAttributes.FirstOrDefault(a => a.Code == def.Code);
            if (stored is null) continue;
            if (!CustomAttributeValidator.TryNormalize(def.Type, stored.Value, out var value))
            {
                onInvalid?.Invoke(def.Code, def.Type);
                continue;
            }
            result.Add(new JsonObject { ["code"] = def.Code, ["value"] = value });
        }
        return result;
    }
}
