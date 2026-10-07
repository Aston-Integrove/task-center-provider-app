using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Tcp.Infrastructure.Scim;

namespace Tcp.Api.Endpoints.Scim;

/// <summary>RFC 7643 section 5-7 discovery documents for the supported subset.</summary>
public static class ScimDiscoveryEndpoints
{
    public static RouteGroupBuilder MapScimDiscovery(this RouteGroupBuilder scim)
    {
        scim.MapGet("/ServiceProviderConfig", (IOptions<ScimOptions> options) =>
        {
            var schemes = new JsonArray(new JsonObject
            {
                ["type"] = "oauthbearertoken", ["name"] = "OAuth Bearer Token",
                ["description"] = "Authentication scheme using the OAuth 2.0 Bearer Token (client_credentials, scope scim)",
                ["specUri"] = "https://www.rfc-editor.org/info/rfc6750", ["primary"] = true,
            });
            if (options.Value.AllowBasic)
                schemes.Add(new JsonObject
                {
                    ["type"] = "httpbasic", ["name"] = "HTTP Basic",
                    ["description"] = "HTTP Basic authentication with the SCIM client credentials",
                    ["specUri"] = "https://www.rfc-editor.org/info/rfc2617", ["primary"] = false,
                });

            return ScimResults.Json(200, new JsonObject
            {
                ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig"),
                ["documentationUri"] = "https://www.rfc-editor.org/rfc/rfc7644",
                ["patch"] = new JsonObject { ["supported"] = true },
                ["bulk"] = new JsonObject { ["supported"] = false, ["maxOperations"] = 0, ["maxPayloadSize"] = 0 },
                ["filter"] = new JsonObject { ["supported"] = true, ["maxResults"] = options.Value.MaxCount },
                ["changePassword"] = new JsonObject { ["supported"] = false },
                ["sort"] = new JsonObject { ["supported"] = false },
                ["etag"] = new JsonObject { ["supported"] = false },
                ["authenticationSchemes"] = schemes,
                ["meta"] = new JsonObject { ["resourceType"] = "ServiceProviderConfig", ["location"] = "/scim/v2/ServiceProviderConfig" },
            });
        });

        scim.MapGet("/ResourceTypes", () => ScimResults.Json(200, ListOf(
            ResourceType("User", "/Users", ScimSchemas.User, "User Account", ScimSchemas.SapUser),
            ResourceType("Group", "/Groups", ScimSchemas.Group, "Group", null))));

        scim.MapGet("/Schemas", () => ScimResults.Json(200, ListOf(
            Schema(ScimSchemas.User, "User", "User Account",
                Attr("userName", "string", required: true, uniqueness: "server"),
                Attr("externalId", "string"),
                Attr("displayName", "string"),
                Complex("name", false, Attr("givenName", "string"), Attr("familyName", "string"), Attr("formatted", "string", mutability: "readOnly")),
                Complex("emails", true, Attr("value", "string"), Attr("type", "string"), Attr("primary", "boolean")),
                Attr("active", "boolean")),
            Schema(ScimSchemas.Group, "Group", "Group",
                Attr("displayName", "string", required: true, uniqueness: "server"),
                Attr("externalId", "string"),
                Complex("members", true, Attr("value", "string", mutability: "immutable"), Attr("display", "string", mutability: "readOnly"), Attr("$ref", "reference", mutability: "immutable"))),
            Schema(ScimSchemas.SapUser, "SAP User", "SAP extension carrying the IAS Global User ID",
                Attr("userUuid", "string", description: "IAS Global User ID"),
                Attr("userId", "string")))));

        return scim;
    }

    private static JsonObject ListOf(params JsonObject[] resources) => new()
    {
        ["schemas"] = new JsonArray(ScimSchemas.ListResponse),
        ["totalResults"] = resources.Length,
        ["startIndex"] = 1,
        ["itemsPerPage"] = resources.Length,
        ["Resources"] = new JsonArray(resources.Cast<JsonNode>().ToArray()),
    };

    private static JsonObject ResourceType(string name, string endpoint, string schema, string description, string? extension)
    {
        var result = new JsonObject
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:ResourceType"),
            ["id"] = name, ["name"] = name, ["endpoint"] = endpoint, ["description"] = description, ["schema"] = schema,
            ["meta"] = new JsonObject { ["resourceType"] = "ResourceType", ["location"] = $"/scim/v2/ResourceTypes/{name}" },
        };
        if (extension is not null)
            result["schemaExtensions"] = new JsonArray(new JsonObject { ["schema"] = extension, ["required"] = false });
        return result;
    }

    private static JsonObject Schema(string id, string name, string description, params JsonObject[] attributes) => new()
    {
        ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:Schema"),
        ["id"] = id, ["name"] = name, ["description"] = description,
        ["attributes"] = new JsonArray(attributes.Cast<JsonNode>().ToArray()),
        ["meta"] = new JsonObject { ["resourceType"] = "Schema", ["location"] = $"/scim/v2/Schemas/{id}" },
    };

    private static JsonObject Attr(string name, string type, bool required = false, string mutability = "readWrite",
        string uniqueness = "none", string? description = null) => new()
    {
        ["name"] = name, ["type"] = type, ["multiValued"] = false, ["description"] = description ?? name,
        ["required"] = required, ["caseExact"] = false, ["mutability"] = mutability, ["returned"] = "default", ["uniqueness"] = uniqueness,
    };

    private static JsonObject Complex(string name, bool multi, params JsonObject[] sub) => new()
    {
        ["name"] = name, ["type"] = "complex", ["multiValued"] = multi, ["description"] = name, ["required"] = false,
        ["mutability"] = "readWrite", ["returned"] = "default", ["uniqueness"] = "none",
        ["subAttributes"] = new JsonArray(sub.Cast<JsonNode>().ToArray()),
    };
}
