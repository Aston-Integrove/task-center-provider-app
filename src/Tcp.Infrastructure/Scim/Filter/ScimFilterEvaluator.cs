using System.Text.Json.Nodes;

namespace Tcp.Infrastructure.Scim.Filter;

/// <summary>
/// Evaluates a filter against an in-memory JSON object. Used by PATCH to pick the items of a multi-valued
/// attribute (<c>emails[type eq "work"]</c>, <c>members[value eq "x"]</c>). Strings compare case-insensitively.
/// </summary>
public static class ScimFilterEvaluator
{
    public static bool Matches(JsonNode? item, FilterNode filter) => filter switch
    {
        AndNode a => Matches(item, a.Left) && Matches(item, a.Right),
        OrNode o => Matches(item, o.Left) || Matches(item, o.Right),
        NotNode n => !Matches(item, n.Inner),
        PresentNode p => IsPresent(Read(item, p.Path)),
        CompareNode c => Compare(Read(item, c.Path), c.Op, c.Value),
        _ => false,
    };

    private static JsonNode? Read(JsonNode? item, AttrPath path)
    {
        if (item is not JsonObject obj) return null;
        var node = Find(obj, path.Name);
        return path.Sub is null ? node : node is JsonObject inner ? Find(inner, path.Sub) : null;
    }

    private static JsonNode? Find(JsonObject obj, string name)
    {
        foreach (var (key, value) in obj)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    private static bool IsPresent(JsonNode? node) => node switch
    {
        null => false,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonArray a => a.Count > 0,
        _ => true,
    };

    private static bool Compare(JsonNode? actual, string op, FilterValue expected)
    {
        if (expected.Kind == FilterValueKind.Null)
            return op == "eq" ? !IsPresent(actual) : op == "ne" && IsPresent(actual);

        if (actual is not JsonValue value) return op == "ne";

        switch (expected.Kind)
        {
            case FilterValueKind.Boolean:
                if (!value.TryGetValue<bool>(out var b)) return op == "ne";
                return op switch { "eq" => b == expected.Boolean, "ne" => b != expected.Boolean, _ => false };

            case FilterValueKind.Number:
                if (!value.TryGetValue<double>(out var d)) return op == "ne";
                return op switch
                {
                    "eq" => d == expected.Number, "ne" => d != expected.Number,
                    "gt" => d > expected.Number, "ge" => d >= expected.Number,
                    "lt" => d < expected.Number, "le" => d <= expected.Number,
                    _ => false,
                };

            default:
                var s = value.TryGetValue<string>(out var str) ? str : value.ToString();
                var e = expected.Text ?? string.Empty;
                var cmp = string.Compare(s, e, StringComparison.OrdinalIgnoreCase);
                return op switch
                {
                    "eq" => cmp == 0,
                    "ne" => cmp != 0,
                    "co" => s.Contains(e, StringComparison.OrdinalIgnoreCase),
                    "sw" => s.StartsWith(e, StringComparison.OrdinalIgnoreCase),
                    "ew" => s.EndsWith(e, StringComparison.OrdinalIgnoreCase),
                    "gt" => cmp > 0, "ge" => cmp >= 0, "lt" => cmp < 0, "le" => cmp <= 0,
                    _ => false,
                };
        }
    }
}
