namespace Tcp.Domain.Identity;

public enum GlobalUserIdSource
{
    /// <summary>SAP extension attribute <c>userUuid</c>.</summary>
    UserUuid,

    /// <summary><c>externalId</c>, only when it is a GUID.</summary>
    ExternalId,

    /// <summary>The <c>id</c> supplied in the payload by the IPS transformation, only when it is a GUID.</summary>
    Id,
}

/// <summary>
/// FR-SCIM-07: derives the Global User ID from the SCIM payload using a configurable source order; the first
/// source holding a valid GUID wins and the result is normalised to a lower-case hyphenated GUID.
/// </summary>
public sealed class GlobalUserIdPolicy(IEnumerable<GlobalUserIdSource> order)
{
    private readonly GlobalUserIdSource[] _order = [.. order];

    public static GlobalUserIdPolicy Default { get; } = new([GlobalUserIdSource.UserUuid, GlobalUserIdSource.ExternalId]);

    public IReadOnlyList<GlobalUserIdSource> Order => _order;

    /// <summary>Parses configuration names (<c>userUuid</c>, <c>externalId</c>, <c>id</c>); unknown names are an error.</summary>
    public static GlobalUserIdPolicy Parse(IEnumerable<string> names)
    {
        var list = new List<GlobalUserIdSource>();
        foreach (var name in names)
        {
            if (!Enum.TryParse<GlobalUserIdSource>(name?.Trim(), ignoreCase: true, out var source))
                throw new ArgumentException($"Unknown Global User ID source '{name}' (expected userUuid, externalId or id)");
            list.Add(source);
        }
        return list.Count == 0 ? Default : new GlobalUserIdPolicy(list);
    }

    public string? Derive(string? userUuid, string? externalId, string? payloadId)
    {
        foreach (var source in _order)
        {
            var candidate = source switch
            {
                GlobalUserIdSource.UserUuid => userUuid,
                GlobalUserIdSource.ExternalId => externalId,
                GlobalUserIdSource.Id => payloadId,
                _ => null,
            };
            if (GlobalUserId.TryNormalize(candidate, out var normalized)) return normalized;
        }
        return null;
    }
}
