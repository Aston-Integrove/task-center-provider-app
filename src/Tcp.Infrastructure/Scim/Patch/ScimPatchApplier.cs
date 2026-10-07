using System.Text.Json.Nodes;
using Tcp.Infrastructure.Scim.Filter;

namespace Tcp.Infrastructure.Scim.Patch;

/// <summary>
/// RFC 7644 section 3.5.2 PatchOp applied to the JSON representation of a resource. The result is validated
/// by the same mapping code as PUT, so there is a single validation path (plan 003, decision 1).
/// Supports add/replace/remove with or without <c>path</c>, URN-qualified paths, sub-attributes and value filters.
/// </summary>
public static class ScimPatchApplier
{
    public const string PatchOpUrn = "urn:ietf:params:scim:api:messages:2.0:PatchOp";

    public static JsonObject Apply(JsonObject resource, JsonObject patch)
    {
        if (patch["Operations"] is not JsonArray operations || operations.Count == 0)
            throw ScimException.BadRequest("invalidSyntax", "PatchOp requires a non-empty Operations array");

        var result = (JsonObject)resource.DeepClone();
        foreach (var node in operations)
        {
            if (node is not JsonObject op) throw ScimException.BadRequest("invalidSyntax", "Each operation must be an object");
            var name = (op["op"] as JsonValue)?.TryGetValue<string>(out var s) == true ? s.ToLowerInvariant() : null;
            if (name is not ("add" or "replace" or "remove"))
                throw ScimException.BadRequest("invalidSyntax", "op must be add, replace or remove");

            var path = (op["path"] as JsonValue)?.TryGetValue<string>(out var p) == true ? p : null;
            var value = op["value"];

            if (string.IsNullOrWhiteSpace(path)) ApplyWithoutPath(result, name, value);
            else ApplyWithPath(result, name, ScimFilterParser.ParseAttrPath(path), value);
        }
        return result;
    }

    // ---- no path ------------------------------------------------------------------------------

    private static void ApplyWithoutPath(JsonObject resource, string op, JsonNode? value)
    {
        if (op == "remove") throw ScimException.BadRequest("noTarget", "remove requires a path");
        if (value is not JsonObject values)
            throw ScimException.BadRequest("invalidValue", $"{op} without a path requires an object value");

        foreach (var (key, v) in values)
        {
            if (key.StartsWith("urn:", StringComparison.OrdinalIgnoreCase) && v is JsonObject extension)
            {
                var container = GetOrCreateObject(resource, key);
                foreach (var (ek, ev) in extension) SetSimple(container, ek, ev, op);
                continue;
            }

            AttrPath path;
            try { path = ScimFilterParser.ParseAttrPath(key); }
            catch (ScimException) { path = new AttrPath(null, key, null, null); }
            ApplyWithPath(resource, op, path, v);
        }
    }

    // ---- with path ----------------------------------------------------------------------------

    private static void ApplyWithPath(JsonObject resource, string op, AttrPath path, JsonNode? value)
    {
        var container = path.Schema is null ? resource : GetOrCreateObject(resource, path.Schema);

        if (path.ValueFilter is not null)
        {
            ApplyToFilteredItems(container, op, path, value);
            return;
        }

        if (path.Sub is not null)
        {
            var key = FindKey(container, path.Name);
            if (key is not null && container[key] is JsonArray array)
            {
                foreach (var item in array.OfType<JsonObject>()) SetOrRemove(item, path.Sub, value, op);
                return;
            }
            SetOrRemove(GetOrCreateObject(container, path.Name), path.Sub, value, op);
            return;
        }

        if (op == "remove") RemoveSimple(container, path.Name, value);
        else SetSimple(container, path.Name, value, op);
    }

    private static void ApplyToFilteredItems(JsonObject container, string op, AttrPath path, JsonNode? value)
    {
        var key = FindKey(container, path.Name);
        if (key is null || container[key] is not JsonArray array)
            throw ScimException.BadRequest("noTarget", $"'{path.Name}' has no values to match");

        var matches = array.Select((item, index) => (item, index))
            .Where(t => ScimFilterEvaluator.Matches(t.item, path.ValueFilter!)).ToList();

        if (op == "remove")
        {
            if (path.Sub is null)
            {
                foreach (var (item, _) in matches.OrderByDescending(m => m.index)) array.Remove(item);
            }
            else
            {
                foreach (var (item, _) in matches)
                    if (item is JsonObject obj) RemoveKey(obj, path.Sub);
            }
            return;
        }

        if (matches.Count == 0) throw ScimException.BadRequest("noTarget", "The filter matched no values");

        foreach (var (item, index) in matches)
        {
            if (path.Sub is not null)
            {
                if (item is JsonObject obj) SetOrRemove(obj, path.Sub, value, op);
            }
            else if (value is JsonObject replacement)
            {
                array[index] = replacement.DeepClone();
            }
            else
            {
                throw ScimException.BadRequest("invalidValue", "Replacing a filtered value requires an object");
            }
        }
    }

    // ---- primitives ---------------------------------------------------------------------------

    private static void SetOrRemove(JsonObject target, string name, JsonNode? value, string op)
    {
        if (op == "remove") RemoveKey(target, name);
        else target[FindKey(target, name) ?? name] = value?.DeepClone();
    }

    /// <summary>add: append to arrays / merge objects / set scalars; replace: overwrite.</summary>
    private static void SetSimple(JsonObject container, string name, JsonNode? value, string op)
    {
        var key = FindKey(container, name) ?? name;
        var existing = container[key];

        if (op == "add" && existing is JsonArray array)
        {
            foreach (var item in value is JsonArray incoming ? incoming : [value])
            {
                if (item is null) continue;
                if (!array.Any(e => JsonNode.DeepEquals(e, item))) array.Add(item.DeepClone());
            }
            return;
        }

        if (op == "add" && existing is JsonObject current && value is JsonObject extra)
        {
            foreach (var (k, v) in extra) current[FindKey(current, k) ?? k] = v?.DeepClone();
            return;
        }

        if (op == "add" && existing is null && value is JsonObject or JsonValue && IsMultiValued(name) && value is not JsonArray)
        {
            container[key] = new JsonArray(value!.DeepClone());
            return;
        }

        container[key] = value?.DeepClone();
    }

    private static void RemoveSimple(JsonObject container, string name, JsonNode? value)
    {
        var key = FindKey(container, name);
        if (key is null) return;

        // remove with a value on a multi-valued attribute removes just those values (members, emails)
        if (value is not null && container[key] is JsonArray array)
        {
            var toRemove = value is JsonArray arr ? arr : [value];
            foreach (var r in toRemove)
            {
                foreach (var item in array.Where(i => SameValue(i, r)).ToList()) array.Remove(item);
            }
            return;
        }
        container.Remove(key);
    }

    private static bool SameValue(JsonNode? item, JsonNode? candidate)
    {
        if (JsonNode.DeepEquals(item, candidate)) return true;
        return item is JsonObject a && candidate is JsonObject b &&
               a["value"] is { } av && b["value"] is { } bv && JsonNode.DeepEquals(av, bv);
    }

    private static bool IsMultiValued(string name) =>
        name.Equals("emails", StringComparison.OrdinalIgnoreCase) || name.Equals("members", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("phoneNumbers", StringComparison.OrdinalIgnoreCase);

    private static void RemoveKey(JsonObject obj, string name)
    {
        var key = FindKey(obj, name);
        if (key is not null) obj.Remove(key);
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string name)
    {
        var key = FindKey(parent, name);
        if (key is not null && parent[key] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[key ?? name] = created;
        return created;
    }

    private static string? FindKey(JsonObject obj, string name)
    {
        foreach (var (key, _) in obj)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return key;
        return null;
    }
}
