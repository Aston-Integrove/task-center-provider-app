using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tcp.Domain.Identity;
using Tcp.Domain.Tasks;
using Tcp.Infrastructure.Persistence;
using Tcp.Infrastructure.Scim;

namespace Tcp.Infrastructure.Tasks;

/// <summary>
/// The task side of GDPR erasure and group deletion (FR-SCIM-12). Runs inside the transaction opened by the SCIM
/// service. Every touched task gets a fresh <c>modifiedAt</c> so Task Center's next delta pull picks it up, and no
/// task is ever hard-deleted — removal is a CANCELED tombstone (constitution VII).
/// </summary>
public sealed class TaskIdentityReferences(TcpDbContext db) : ITaskIdentityReferences
{
    public async Task<bool> IsUserReferencedAsync(string globalUserId, CancellationToken ct) =>
        await db.Tasks.AnyAsync(t => t.CreatedBy == globalUserId || t.ModifiedBy == globalUserId ||
                                     t.Processor == globalUserId || t.CompletedBy == globalUserId, ct)
        || await db.TaskRecipientUsers.AnyAsync(r => r.GlobalUserId == globalUserId, ct)
        || await db.TaskOperationErrors.AnyAsync(e => e.ExecutedBy == globalUserId, ct);

    public async Task AnonymiseUserAsync(string gid, DateTime nowUtc, CancellationToken ct)
    {
        var now = TaskInstance.TruncateToMs(nowUtc);

        // 1. Open tasks whose only recipient is this user are tombstoned.
        var tombstones = await db.Tasks
            .Where(t => t.Status != TaskStatuses.Completed && t.Status != TaskStatuses.Canceled)
            .Where(t => t.RecipientUsers.Any(r => r.GlobalUserId == gid) && t.RecipientUsers.Count() == 1 && !t.RecipientGroups.Any())
            .Select(t => t.Urn).ToListAsync(ct);

        // 2. Everything that mentions the user must be rewritten (and therefore re-published).
        var touched = new HashSet<string>(tombstones, StringComparer.Ordinal);
        touched.UnionWith(await db.Tasks.Where(t => t.CreatedBy == gid || t.ModifiedBy == gid || t.Processor == gid || t.CompletedBy == gid)
            .Select(t => t.Urn).ToListAsync(ct));
        touched.UnionWith(await db.TaskRecipientUsers.Where(r => r.GlobalUserId == gid).Select(r => r.TaskUrn).ToListAsync(ct));
        touched.UnionWith(await db.TaskOperationErrors.Where(e => e.ExecutedBy == gid).Select(e => e.TaskUrn).ToListAsync(ct));

        if (tombstones.Count > 0)
            await db.Tasks.Where(t => tombstones.Contains(t.Urn))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TaskStatuses.Canceled), ct);

        await db.Tasks.Where(t => t.CreatedBy == gid || t.ModifiedBy == gid || t.Processor == gid || t.CompletedBy == gid)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.CreatedBy, t => t.CreatedBy == gid ? null : t.CreatedBy)
                .SetProperty(t => t.ModifiedBy, t => t.ModifiedBy == gid ? null : t.ModifiedBy)
                .SetProperty(t => t.Processor, t => t.Processor == gid ? null : t.Processor)
                .SetProperty(t => t.CompletedBy, t => t.CompletedBy == gid ? null : t.CompletedBy), ct);

        await db.TaskRecipientUsers.Where(r => r.GlobalUserId == gid).ExecuteDeleteAsync(ct);
        await db.TaskOperationErrors.Where(e => e.ExecutedBy == gid).ExecuteDeleteAsync(ct);

        // The audit trail stays, but no longer identifies the person.
        await db.OperationLog.Where(o => o.UserId == gid)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.UserId, "erased").SetProperty(o => o.Comment, (string?)null), ct);

        await TouchAsync(touched, now, ct);
    }

    public async Task RemoveGroupAsync(string groupName, DateTime nowUtc, CancellationToken ct)
    {
        var now = TaskInstance.TruncateToMs(nowUtc);
        var touched = await db.TaskRecipientGroups.Where(r => r.GroupName == groupName).Select(r => r.TaskUrn).ToListAsync(ct);
        if (touched.Count == 0) return;

        await db.TaskRecipientGroups.Where(r => r.GroupName == groupName).ExecuteDeleteAsync(ct);
        await TouchAsync(touched, now, ct);
    }

    /// <summary>Bumps <c>modifiedAt</c> (monotonic: previous + 1 ms if the clock did not advance) and clears <c>modifiedBy</c>.</summary>
    private async Task TouchAsync(IReadOnlyCollection<string> urns, DateTime now, CancellationToken ct)
    {
        foreach (var chunk in urns.Chunk(500))
        {
            var batch = chunk.ToList();
            await db.Tasks.Where(t => batch.Contains(t.Urn)).ExecuteUpdateAsync(s => s
                .SetProperty(t => t.ModifiedAt, t => t.ModifiedAt >= now ? t.ModifiedAt.AddMilliseconds(1) : now)
                .SetProperty(t => t.ModifiedBy, (string?)null), ct);
        }
    }
}

public static class TaskServiceCollectionExtensions
{
    /// <summary>Registers the task repositories and replaces the no-op identity references with the real ones.</summary>
    public static IServiceCollection AddTaskInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<TaskRepository>();
        services.AddScoped<DefinitionRepository>();
        services.AddScoped<DefinitionSeeder>();
        services.AddScoped<LocalIdGenerator>();
        services.RemoveAll<ITaskIdentityReferences>();
        services.AddScoped<ITaskIdentityReferences, TaskIdentityReferences>();
        return services;
    }
}
