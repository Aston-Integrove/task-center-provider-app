using System.Globalization;
using System.Text.Json.Nodes;
using Tcp.Domain.Identity;
using Tcp.Infrastructure.Scim.Filter;

namespace Tcp.Infrastructure.Scim;

public static class ScimSchemas
{
    public const string User = "urn:ietf:params:scim:schemas:core:2.0:User";
    public const string Group = "urn:ietf:params:scim:schemas:core:2.0:Group";
    public const string SapUser = ScimFilterToLinq.SapExtensionUrn;
    public const string ListResponse = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    public const string Error = "urn:ietf:params:scim:api:messages:2.0:Error";
}

/// <summary>Validated, flattened view of a SCIM user payload (what PUT/POST/PATCH all reduce to).</summary>
public sealed record UserWrite(
    string UserName,
    string? ExternalId,
    string? DisplayName,
    string? GivenName,
    string? FamilyName,
    string? PrimaryEmail,
    string EmailsJson,
    string EmailsSearch,
    bool Active,
    string? UserUuid,
    string? PayloadId,
    string RawJson);

internal static class Json
{
    public static JsonNode? Get(JsonObject obj, string name)
    {
        foreach (var (key, value) in obj)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    public static string? OptionalString(JsonObject obj, string name, int maxLength)
    {
        var node = Get(obj, name);
        if (node is null) return null;
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s))
            throw ScimException.BadRequest("invalidValue", $"'{name}' must be a string");
        s = s.Trim();
        if (s.Length == 0) return null;
        if (s.Length > maxLength) throw ScimException.BadRequest("invalidValue", $"'{name}' is longer than {maxLength} characters");
        return s;
    }

    public static bool? OptionalBool(JsonObject obj, string name)
    {
        var node = Get(obj, name);
        if (node is null) return null;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            // Some IdPs (e.g. Azure AD) send booleans as strings.
            if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed)) return parsed;
        }
        throw ScimException.BadRequest("invalidValue", $"'{name}' must be a boolean");
    }

    public static string Timestamp(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}

public static class ScimUserMapper
{
    public static UserWrite Parse(JsonObject payload)
    {
        var userName = Json.OptionalString(payload, "userName", 256)
            ?? throw ScimException.BadRequest("invalidValue", "userName is required");

        string? given = null, family = null;
        if (Json.Get(payload, "name") is { } nameNode)
        {
            if (nameNode is not JsonObject name) throw ScimException.BadRequest("invalidValue", "'name' must be an object");
            given = Json.OptionalString(name, "givenName", 128);
            family = Json.OptionalString(name, "familyName", 128);
        }

        var emails = new List<(string Value, string? Type, bool Primary)>();
        if (Json.Get(payload, "emails") is { } emailsNode)
        {
            if (emailsNode is not JsonArray array) throw ScimException.BadRequest("invalidValue", "'emails' must be an array");
            foreach (var item in array)
            {
                if (item is not JsonObject e) throw ScimException.BadRequest("invalidValue", "Each email must be an object");
                var value = Json.OptionalString(e, "value", 320);
                if (value is null) continue;
                emails.Add((value, Json.OptionalString(e, "type", 64), Json.OptionalBool(e, "primary") ?? false));
            }
        }

        var primary = emails.FirstOrDefault(e => e.Primary);
        if (primary.Value is null && emails.Count > 0) primary = emails[0];

        string? userUuid = null;
        if (Json.Get(payload, ScimSchemas.SapUser) is JsonObject ext)
            userUuid = Json.OptionalString(ext, "userUuid", 64);

        var emailsJson = new JsonArray(emails.Select(e =>
        {
            var o = new JsonObject { ["value"] = e.Value };
            if (e.Type is not null) o["type"] = e.Type;
            if (e.Primary) o["primary"] = true;
            return (JsonNode)o;
        }).ToArray());

        return new UserWrite(
            userName,
            Json.OptionalString(payload, "externalId", 256),
            Json.OptionalString(payload, "displayName", 256),
            given, family,
            primary.Value,
            emailsJson.ToJsonString(),
            ScimFilterToLinq.BuildEmailsSearch(emails.Select(e => ((string?)e.Type, e.Value))),
            Json.OptionalBool(payload, "active") ?? true,
            userUuid,
            Json.OptionalString(payload, "id", 64),
            payload.ToJsonString());
    }

    public static JsonObject ToJson(ScimUser u, string baseUrl)
    {
        var schemas = new JsonArray(ScimSchemas.User);
        var result = new JsonObject { ["schemas"] = schemas, ["id"] = u.Id.ToString("D") };

        if (u.ExternalId is not null) result["externalId"] = u.ExternalId;
        result["userName"] = u.UserName;
        if (u.DisplayName is not null) result["displayName"] = u.DisplayName;

        if (u.GivenName is not null || u.FamilyName is not null)
        {
            var name = new JsonObject();
            if (u.GivenName is not null) name["givenName"] = u.GivenName;
            if (u.FamilyName is not null) name["familyName"] = u.FamilyName;
            name["formatted"] = string.Join(' ', new[] { u.GivenName, u.FamilyName }.Where(s => !string.IsNullOrEmpty(s)));
            result["name"] = name;
        }

        if (JsonNode.Parse(u.EmailsJson) is JsonArray { Count: > 0 } emails) result["emails"] = emails;
        result["active"] = u.Active;

        if (u.GlobalUserId is not null)
        {
            schemas.Add(ScimSchemas.SapUser);
            var ext = new JsonObject { ["userUuid"] = u.GlobalUserId };
            // echo the SAP userId if IPS sent one
            if (JsonNode.Parse(u.RawJson) is JsonObject raw && Json.Get(raw, ScimSchemas.SapUser) is JsonObject rawExt &&
                Json.Get(rawExt, "userId") is JsonValue uid && uid.TryGetValue<string>(out var userId))
                ext["userId"] = userId;
            result[ScimSchemas.SapUser] = ext;
        }

        result["meta"] = new JsonObject
        {
            ["resourceType"] = "User",
            ["created"] = Json.Timestamp(u.Created),
            ["lastModified"] = Json.Timestamp(u.LastModified),
            ["location"] = $"{baseUrl}/scim/v2/Users/{u.Id:D}",
        };
        return result;
    }
}

public sealed record GroupWrite(string DisplayName, string? ExternalId, IReadOnlyList<string> MemberValues);

public static class ScimGroupMapper
{
    public static GroupWrite Parse(JsonObject payload)
    {
        var displayName = Json.OptionalString(payload, "displayName", 256)
            ?? throw ScimException.BadRequest("invalidValue", "displayName is required");

        var members = new List<string>();
        if (Json.Get(payload, "members") is { } node)
        {
            if (node is not JsonArray array) throw ScimException.BadRequest("invalidValue", "'members' must be an array");
            foreach (var item in array)
            {
                if (item is not JsonObject m || Json.OptionalString(m, "value", 64) is not { } value)
                    throw ScimException.BadRequest("invalidValue", "Each member requires a value");
                members.Add(value);
            }
        }
        return new GroupWrite(displayName, Json.OptionalString(payload, "externalId", 256), members);
    }

    public static JsonObject ToJson(ScimGroup g, string baseUrl, bool includeMembers = true)
    {
        var result = new JsonObject { ["schemas"] = new JsonArray(ScimSchemas.Group), ["id"] = g.Id.ToString("D") };
        if (g.ExternalId is not null) result["externalId"] = g.ExternalId;
        result["displayName"] = g.DisplayName;

        if (includeMembers)
        {
            result["members"] = new JsonArray(g.Members
                .OrderBy(m => m.User.UserName, StringComparer.OrdinalIgnoreCase)
                .Select(m => (JsonNode)new JsonObject
                {
                    ["value"] = m.UserId.ToString("D"),
                    ["display"] = m.User.DisplayName ?? m.User.UserName,
                    ["$ref"] = $"{baseUrl}/scim/v2/Users/{m.UserId:D}",
                }).ToArray());
        }

        result["meta"] = new JsonObject
        {
            ["resourceType"] = "Group",
            ["created"] = Json.Timestamp(g.Created),
            ["lastModified"] = Json.Timestamp(g.LastModified),
            ["location"] = $"{baseUrl}/scim/v2/Groups/{g.Id:D}",
        };
        return result;
    }
}
