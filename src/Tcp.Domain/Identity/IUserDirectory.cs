namespace Tcp.Domain.Identity;

public sealed record DirectoryUser(
    string GlobalUserId,
    string UserName,
    string? Email,
    string? DisplayName,
    bool Active);

/// <summary>
/// Read access to the SCIM-provisioned user store (spec 003, FR-SCIM-09), consumed by the token
/// service (Global User ID resolution) and the SPI (recipient/entitlement checks).
/// </summary>
public interface IUserDirectory
{
    Task<DirectoryUser?> FindByGlobalUserIdAsync(string globalUserId, CancellationToken ct = default);

    /// <summary>Returns the user only when exactly one non-deleted user has this e-mail (case-insensitive).</summary>
    Task<DirectoryUser?> FindUniqueByEmailAsync(string email, CancellationToken ct = default);

    Task<bool> IsMemberOfAnyGroupAsync(string globalUserId, IReadOnlyCollection<string> groupNames, CancellationToken ct = default);

    Task<IReadOnlyList<DirectoryUser>> ListActiveAsync(CancellationToken ct = default);
}

/// <summary>Default until the SCIM store is registered: no users exist.</summary>
public sealed class EmptyUserDirectory : IUserDirectory
{
    public Task<DirectoryUser?> FindByGlobalUserIdAsync(string globalUserId, CancellationToken ct = default) => Task.FromResult<DirectoryUser?>(null);
    public Task<DirectoryUser?> FindUniqueByEmailAsync(string email, CancellationToken ct = default) => Task.FromResult<DirectoryUser?>(null);
    public Task<bool> IsMemberOfAnyGroupAsync(string globalUserId, IReadOnlyCollection<string> groupNames, CancellationToken ct = default) => Task.FromResult(false);
    public Task<IReadOnlyList<DirectoryUser>> ListActiveAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DirectoryUser>>([]);
}
