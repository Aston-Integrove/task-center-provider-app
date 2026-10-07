using System.Text.Json.Nodes;

namespace Tcp.Domain.Tasks;

public sealed record LocalizedText(string LanguageCode, string Text);

public sealed record ReasonDefinition(string Code, IReadOnlyList<LocalizedText> Name);

public sealed record OperationDefinition(
    string Code,
    IReadOnlyList<LocalizedText> Name,
    string Nature,
    string CommentRequired,
    string ReasonRequired,
    IReadOnlyList<ReasonDefinition> PossibleReasons,
    JsonArray? Capabilities);

public sealed record CustomAttributeDefinition(
    string Code,
    string Type,
    string? Format,
    IReadOnlyList<LocalizedText> Name,
    int Rank);

/// <summary>Stored form of a task definition (table tc.TaskDefinition): SAP-shaped JSON in a few columns.</summary>
public class TaskDefinitionEntity
{
    public string Urn { get; set; } = "";
    public string LocalId { get; set; } = "";
    public string NameJson { get; set; } = "[]";
    public string ResponsesJson { get; set; } = "[]";
    public string ActionsJson { get; set; } = "[]";
    public string CustomAttributesJson { get; set; } = "[]";
    public string CapabilitiesJson { get; set; } = "[]";
    public string? TaskDetailsSettingsJson { get; set; }
    public DateTime ModifiedAt { get; set; }
}

/// <summary>Parsed, immutable task definition used by the rules and the SPI mapper.</summary>
public sealed class TaskDefinition
{
    public required string Urn { get; init; }
    public required string LocalId { get; init; }
    public required IReadOnlyList<LocalizedText> Name { get; init; }
    public IReadOnlyList<OperationDefinition> Responses { get; init; } = [];
    public IReadOnlyList<OperationDefinition> Actions { get; init; } = [];
    public IReadOnlyList<CustomAttributeDefinition> CustomAttributes { get; init; } = [];
    public JsonArray Capabilities { get; init; } = [];
    public JsonNode? TaskDetailsSettings { get; init; }

    public OperationDefinition? FindResponse(string code) => Responses.FirstOrDefault(r => r.Code == code);
    public OperationDefinition? FindAction(string code) => Actions.FirstOrDefault(a => a.Code == code);
    public CustomAttributeDefinition? FindAttribute(string code) => CustomAttributes.FirstOrDefault(a => a.Code == code);

    public static TaskDefinition FromEntity(TaskDefinitionEntity e) => new()
    {
        Urn = e.Urn,
        LocalId = e.LocalId,
        Name = ParseTexts(JsonNode.Parse(e.NameJson)),
        Responses = ParseOperations(JsonNode.Parse(e.ResponsesJson)),
        Actions = ParseOperations(JsonNode.Parse(e.ActionsJson)),
        CustomAttributes = ParseAttributes(JsonNode.Parse(e.CustomAttributesJson)),
        Capabilities = JsonNode.Parse(e.CapabilitiesJson) as JsonArray ?? [],
        TaskDetailsSettings = e.TaskDetailsSettingsJson is null ? null : JsonNode.Parse(e.TaskDetailsSettingsJson),
    };

    public static IReadOnlyList<LocalizedText> ParseTexts(JsonNode? node) =>
        node is JsonArray array
            ? array.OfType<JsonObject>()
                .Select(o => new LocalizedText(Str(o, "languageCode"), Str(o, "text")))
                .Where(t => t.LanguageCode.Length > 0).ToList()
            : [];

    private static IReadOnlyList<OperationDefinition> ParseOperations(JsonNode? node) =>
        node is JsonArray array
            ? array.OfType<JsonObject>().Select(o => new OperationDefinition(
                Str(o, "code"),
                ParseTexts(o["name"]),
                Str(o, "nature", "NEUTRAL"),
                Str(o, "commentRequired", "UNSUPPORTED"),
                Str(o, "reasonRequired", "UNSUPPORTED"),
                (o["possibleReasons"] as JsonArray)?.OfType<JsonObject>()
                    .Select(r => new ReasonDefinition(Str(r, "code"), ParseTexts(r["name"]))).ToList() ?? [],
                o["capabilities"] as JsonArray)).ToList()
            : [];

    private static IReadOnlyList<CustomAttributeDefinition> ParseAttributes(JsonNode? node) =>
        node is JsonArray array
            ? array.OfType<JsonObject>().Select(o => new CustomAttributeDefinition(
                Str(o, "code"), Str(o, "type", "STRING"), o["format"]?.GetValue<string>(),
                ParseTexts(o["name"]), o["rank"]?.GetValue<int>() ?? 0)).ToList()
            : [];

    private static string Str(JsonObject o, string name, string fallback = "") =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
}
