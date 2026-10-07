using System.Globalization;
using System.Text.Json.Nodes;
using Json.Schema;
using YamlDotNet.RepresentationModel;

namespace Tcp.TestSupport;

/// <summary>
/// Validates real HTTP bodies against an OpenAPI 3.0 document (YAML or JSON). Response/request schemas are
/// located by path template + method + status, <c>$ref</c>s are inlined and the OAS 3.0 dialect
/// (<c>nullable</c>, boolean <c>exclusiveMinimum</c>) is converted to JSON Schema 2020-12.
/// </summary>
public sealed class OpenApiContract
{
    private readonly JsonObject _doc;

    private OpenApiContract(JsonObject doc) => _doc = doc;

    public static string RepositoryRoot { get; } = FindRoot();

    public static OpenApiContract Load(string repoRelativePath)
    {
        var path = Path.Combine(RepositoryRoot, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var text = File.ReadAllText(path);
        var node = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? JsonNode.Parse(text) : YamlToJson(text);
        return new OpenApiContract((JsonObject)node!);
    }

    /// <summary>Returns validation errors (empty = valid).</summary>
    public IReadOnlyList<string> ValidateResponse(string pathTemplate, string method, int status, string body)
    {
        var operation = FindOperation(pathTemplate, method);
        var responses = operation["responses"] as JsonObject
            ?? throw new InvalidOperationException($"{method} {pathTemplate} has no responses");
        var response = (responses[status.ToString(CultureInfo.InvariantCulture)] ?? responses["default"]) as JsonObject;
        if (response is null)
            return [$"{method} {pathTemplate}: status {status} is not declared in the contract"];

        var schemaNode = (response["content"] as JsonObject)?
            .Select(c => (c.Value as JsonObject)?["schema"]).FirstOrDefault(s => s is not null);
        if (schemaNode is null) return [];

        return Validate(schemaNode, body);
    }

    public IReadOnlyList<string> ValidateRequest(string pathTemplate, string method, string body)
    {
        var operation = FindOperation(pathTemplate, method);
        var schemaNode = ((operation["requestBody"] as JsonObject)?["content"] as JsonObject)?
            .Select(c => (c.Value as JsonObject)?["schema"]).FirstOrDefault(s => s is not null);
        return schemaNode is null ? [] : Validate(schemaNode, body);
    }

    /// <summary>Validates <paramref name="body"/> against a named component schema.</summary>
    public IReadOnlyList<string> ValidateSchema(string schemaName, string body) =>
        Validate(new JsonObject { ["$ref"] = $"#/components/schemas/{schemaName}" }, body);

    public static void AssertValid(IReadOnlyList<string> errors, string context = "")
    {
        if (errors.Count > 0)
            throw new Xunit.Sdk.XunitException($"Contract violation {context}:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
    }

    private IReadOnlyList<string> Validate(JsonNode schemaNode, string body)
    {
        var instance = JsonNode.Parse(body);
        var converted = Convert(Inline(schemaNode.DeepClone(), 0));
        var schema = JsonSchema.FromText(converted.ToJsonString());
        var results = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid) return [];

        var errors = new List<string>();
        foreach (var detail in results.Details.Where(d => d.HasErrors))
            foreach (var e in detail.Errors!)
                errors.Add($"{detail.InstanceLocation}: {e.Key}: {e.Value}");
        return errors.Count > 0 ? errors : ["schema validation failed"];
    }

    private JsonObject FindOperation(string pathTemplate, string method)
    {
        var paths = _doc["paths"] as JsonObject ?? throw new InvalidOperationException("contract has no paths");
        var item = paths[pathTemplate] as JsonObject
            ?? throw new InvalidOperationException($"path {pathTemplate} is not in the contract");
        return item[method.ToLowerInvariant()] as JsonObject
            ?? throw new InvalidOperationException($"{method} {pathTemplate} is not in the contract");
    }

    // ---- $ref inlining ------------------------------------------------------------------------

    private JsonNode Inline(JsonNode node, int depth)
    {
        if (depth > 40) throw new InvalidOperationException("$ref nesting too deep (cycle?)");
        switch (node)
        {
            case JsonObject obj:
                if (obj["$ref"] is JsonValue refValue && refValue.TryGetValue<string>(out var reference))
                    return Inline(Resolve(reference).DeepClone(), depth + 1);
                foreach (var key in obj.Select(p => p.Key).ToList())
                    if (obj[key] is { } child && Inline(child, depth + 1) is var inlined && !ReferenceEquals(inlined, child))
                        obj[key] = inlined;
                return obj;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    if (arr[i] is { } item && Inline(item, depth + 1) is var inlined && !ReferenceEquals(inlined, item))
                        arr[i] = inlined;
                return arr;
            default:
                return node;
        }
    }

    private JsonNode Resolve(string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
            throw new InvalidOperationException($"Only local $ref is supported: {reference}");
        JsonNode? current = _doc;
        foreach (var part in reference[2..].Split('/'))
            current = (current as JsonObject)?[part.Replace("~1", "/").Replace("~0", "~")];
        return current ?? throw new InvalidOperationException($"Unresolvable $ref {reference}");
    }

    // ---- OAS 3.0 -> JSON Schema ------------------------------------------------------------------

    private static JsonNode Convert(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                    if (obj[key] is { } child && key != "example" && key != "default" && key != "enum" && key != "const" &&
                        Convert(child) is var converted && !ReferenceEquals(converted, child))
                        obj[key] = converted;

                if (obj["nullable"] is JsonValue n && n.TryGetValue<bool>(out var nullable))
                {
                    obj.Remove("nullable");
                    if (nullable) MakeNullable(obj);
                }
                ConvertExclusive(obj, "exclusiveMinimum", "minimum");
                ConvertExclusive(obj, "exclusiveMaximum", "maximum");
                obj.Remove("example");
                return obj;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    if (arr[i] is { } item && Convert(item) is var converted && !ReferenceEquals(converted, item))
                        arr[i] = converted;
                return arr;
            default:
                return node;
        }
    }

    private static void MakeNullable(JsonObject obj)
    {
        if (obj["type"] is JsonValue t && t.TryGetValue<string>(out var type))
        {
            obj["type"] = new JsonArray(type, "null");
            if (obj["enum"] is JsonArray e && !e.Any(x => x is null)) e.Add(null);
        }
        else if (obj["allOf"] is not null || obj["oneOf"] is not null || obj["anyOf"] is not null)
        {
            var copy = (JsonObject)obj.DeepClone();
            obj.Clear();
            obj["anyOf"] = new JsonArray(copy, new JsonObject { ["type"] = "null" });
        }
    }

    private static void ConvertExclusive(JsonObject obj, string keyword, string bound)
    {
        if (obj[keyword] is JsonValue v && v.TryGetValue<bool>(out var flag))
        {
            obj.Remove(keyword);
            if (flag && obj[bound] is { } b)
            {
                obj[keyword] = b.DeepClone();
                obj.Remove(bound);
            }
        }
    }

    // ---- YAML -> JSON -------------------------------------------------------------------------

    private static JsonNode? YamlToJson(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return ConvertYaml(stream.Documents[0].RootNode);
    }

    private static JsonNode? ConvertYaml(YamlNode node) => node switch
    {
        YamlMappingNode map => ToObject(map),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(ConvertYaml).ToArray()),
        YamlScalarNode scalar => ConvertScalar(scalar),
        _ => null,
    };

    private static JsonObject ToObject(YamlMappingNode map)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in map.Children)
            obj[((YamlScalarNode)k).Value!] = ConvertYaml(v);
        return obj;
    }

    private static JsonNode? ConvertScalar(YamlScalarNode s)
    {
        var value = s.Value;
        if (s.Style != YamlDotNet.Core.ScalarStyle.Plain || value is null) return JsonValue.Create(value);
        if (value is "null" or "~" or "") return null;
        if (value is "true") return JsonValue.Create(true);
        if (value is "false") return JsonValue.Create(false);
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        return JsonValue.Create(value);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tcp.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root (Tcp.sln)");
    }
}
