namespace Tcp.Domain.Identity;

/// <summary>
/// Seam between identity (SCIM) and tasks (SPI). The SCIM services call it inside their transaction so that
/// deleting a user or group keeps Task Center consistent (FR-SCIM-12, constitution VII). Feature 004 provides
/// the real implementation; until then <see cref="NoTaskIdentityReferences"/> is registered.
/// </summary>
public interface ITaskIdentityReferences
{
    /// <summary>True when any task references the user (creator, processor, recipient, ...). Guards Global User ID changes.</summary>
    Task<bool> IsUserReferencedAsync(string globalUserId, CancellationToken ct);

    /// <summary>
    /// GDPR erase: tombstone tasks where the user is the only recipient, remove the user from other recipient
    /// lists, null out creator/processor references, and bump <c>modifiedAt</c> on every touched task.
    /// </summary>
    Task AnonymiseUserAsync(string globalUserId, DateTime nowUtc, CancellationToken ct);

    /// <summary>Removes a deleted group from task recipient groups, bumping <c>modifiedAt</c> on touched tasks.</summary>
    Task RemoveGroupAsync(string groupName, DateTime nowUtc, CancellationToken ct);
}

public sealed class NoTaskIdentityReferences : ITaskIdentityReferences
{
    public Task<bool> IsUserReferencedAsync(string globalUserId, CancellationToken ct) => Task.FromResult(false);
    public Task AnonymiseUserAsync(string globalUserId, DateTime nowUtc, CancellationToken ct) => Task.CompletedTask;
    public Task RemoveGroupAsync(string groupName, DateTime nowUtc, CancellationToken ct) => Task.CompletedTask;
}
