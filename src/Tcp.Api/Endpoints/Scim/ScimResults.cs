using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tcp.Infrastructure.Scim;

namespace Tcp.Api.Endpoints.Scim;

/// <summary>RFC 7644 responses: <c>application/scim+json</c> bodies and the SCIM Error resource (FR-SCIM-03).</summary>
public static class ScimResults
{
    public const string MediaType = "application/scim+json";

    public static IResult Json(int status, JsonNode body, string? location = null) => new ScimJsonResult(status, body, location);

    public static IResult Error(ScimException ex) => Error(ex.Status, ex.ScimType, ex.Message);

    public static IResult Error(int status, string? scimType, string detail)
    {
        var body = new JsonObject
        {
            ["schemas"] = new JsonArray(ScimSchemas.Error),
            ["status"] = status.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (scimType is not null) body["scimType"] = scimType;
        body["detail"] = detail;
        return new ScimJsonResult(status, body, null);
    }

    public static IResult NoContent() => Results.NoContent();

    public static async Task<JsonObject> ReadBodyAsync(HttpRequest request, int maxBytes = 1024 * 1024)
    {
        try
        {
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms);
            if (ms.Length > maxBytes) throw ScimException.BadRequest("invalidSyntax", "Request body too large");
            if (ms.Length == 0) throw ScimException.BadRequest("invalidSyntax", "Request body is empty");
            return JsonNode.Parse(ms.ToArray()) as JsonObject
                ?? throw ScimException.BadRequest("invalidSyntax", "Request body must be a JSON object");
        }
        catch (JsonException ex)
        {
            throw ScimException.BadRequest("invalidSyntax", "Malformed JSON: " + ex.Message);
        }
    }

    private sealed class ScimJsonResult(int status, JsonNode body, string? location) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = status;
            response.ContentType = MediaType + "; charset=utf-8";
            if (location is not null) response.Headers.Location = location;
            await response.Body.WriteAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), httpContext.RequestAborted);
        }
    }
}
