namespace Tcp.Infrastructure.Scim;

/// <summary>RFC 7644 error: HTTP status plus optional <c>scimType</c> (invalidFilter, uniqueness, mutability, ...).</summary>
public sealed class ScimException(int status, string? scimType, string detail) : Exception(detail)
{
    public int Status { get; } = status;
    public string? ScimType { get; } = scimType;

    public static ScimException BadRequest(string scimType, string detail) => new(400, scimType, detail);
    public static ScimException NotFound(string detail) => new(404, null, detail);
    public static ScimException Conflict(string detail) => new(409, "uniqueness", detail);
}
