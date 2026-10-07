namespace Tcp.Domain.Identity;

/// <summary>SCIM user (table idm.ScimUser). <see cref="GlobalUserId"/> is the only key used towards Task Center.</summary>
public class ScimUser
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = "";
    public string? ExternalId { get; set; }

    /// <summary>Lower-case GUID, null if not derivable (only when Scim:RequireGlobalUserId=false) or after GDPR erasure.</summary>
    public string? GlobalUserId { get; set; }

    public string? DisplayName { get; set; }
    public string? GivenName { get; set; }
    public string? FamilyName { get; set; }
    public string? PrimaryEmail { get; set; }
    public string EmailsJson { get; set; } = "[]";

    /// <summary>Searchable form of all e-mails: <c>|type:value|type:value|</c> (supports emails.value filters).</summary>
    public string EmailsSearch { get; set; } = "|";

    public bool Active { get; set; } = true;
    public bool IsDeleted { get; set; }
    public string RawJson { get; set; } = "{}";
    public DateTime Created { get; set; }
    public DateTime LastModified { get; set; }

    public ICollection<ScimGroupMember> Memberships { get; set; } = [];
}

/// <summary>SCIM group (table idm.ScimGroup). <see cref="DisplayName"/> is the value used in <c>recipientGroups</c>.</summary>
public class ScimGroup
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string? ExternalId { get; set; }
    public DateTime Created { get; set; }
    public DateTime LastModified { get; set; }

    public ICollection<ScimGroupMember> Members { get; set; } = [];
}

public class ScimGroupMember
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public ScimGroup Group { get; set; } = null!;
    public ScimUser User { get; set; } = null!;
}

public class ScimAuditEntry
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string ClientId { get; set; } = "";
    public string Operation { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public Guid ResourceId { get; set; }
    public string Summary { get; set; } = "";
}
