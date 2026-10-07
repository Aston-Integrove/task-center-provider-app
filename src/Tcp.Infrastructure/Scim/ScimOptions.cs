namespace Tcp.Infrastructure.Scim;

public sealed class ScimOptions
{
    public const string Section = "Scim";

    /// <summary>Reject users for whom no Global User ID can be derived (US-003-1.5).</summary>
    public bool RequireGlobalUserId { get; set; } = true;

    /// <summary>Use the Global User ID as the SCIM <c>id</c> (FR-SCIM-04).</summary>
    public bool UseGlobalUserIdAsId { get; set; }

    /// <summary>Source order: userUuid, externalId, id. Empty = userUuid then externalId.</summary>
    public List<string> GlobalUserIdSources { get; set; } = [];

    public bool AllowBasic { get; set; }
    public bool PatchReturnsNoContent { get; set; }
    public bool AcceptGlobalUserIdAsMemberValue { get; set; }
    public int DefaultCount { get; set; } = 100;
    public int MaxCount { get; set; } = 1000;
}
