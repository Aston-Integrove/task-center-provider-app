using Microsoft.EntityFrameworkCore;
using Tcp.Domain.Tasks;
using Tcp.Infrastructure.Persistence;

namespace Tcp.Infrastructure.Tasks;

/// <summary>
/// <c>modifiedAfter</c> null = from the beginning; <c>lastId</c> (a task URN) only makes sense with
/// <c>modifiedAfter</c> and switches the comparison from <c>&gt;=</c> to the keyset rule (TC-PULL-02).
/// </summary>
public sealed record PullQuery(DateTime? ModifiedAfter, string? LastId, int Top);

public sealed class TaskRepository(TcpDbContext db)
{
    /// <summary>
    /// Keyset page ordered by <c>(ModifiedAt, Urn)</c>. The WHERE clause is exactly
    /// <c>(ModifiedAt = @after AND Urn &gt; @lastId) OR ModifiedAt &gt; @after</c>, with <c>Urn</c> compared ordinally
    /// (binary collation). Children are loaded with four set-based queries, never per task.
    /// </summary>
    public async Task<IReadOnlyList<TaskInstance>> PullAsync(PullQuery query, CancellationToken ct)
    {
        IQueryable<TaskInstance> tasks = db.Tasks.AsNoTracking();

        if (query.ModifiedAfter is { } after)
        {
            tasks = query.LastId is { } lastId
                ? tasks.Where(t => t.ModifiedAt > after || (t.ModifiedAt == after && string.Compare(t.Urn, lastId) > 0))
                : tasks.Where(t => t.ModifiedAt >= after);
        }

        var page = await tasks.OrderBy(t => t.ModifiedAt).ThenBy(t => t.Urn).Take(query.Top).ToListAsync(ct);
        await LoadChildrenAsync(page, ct);
        return page;
    }

    public async Task<TaskInstance?> FindAsync(string urn, CancellationToken ct, bool track = false)
    {
        var query = db.Tasks.AsQueryable();
        if (!track) query = query.AsNoTracking();
        var task = await query.FirstOrDefaultAsync(t => t.Urn == urn, ct);
        if (task is not null) await LoadChildrenAsync([task], ct, track);
        return task;
    }

    /// <summary>Of the given Global User IDs, those that belong to an active, non-deleted SCIM user.</summary>
    public async Task<IReadOnlySet<string>> ActiveUserIdsAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var list = ids.Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0) return new HashSet<string>();
        var active = await db.ScimUsers.AsNoTracking()
            .Where(u => u.Active && !u.IsDeleted && u.GlobalUserId != null && list.Contains(u.GlobalUserId))
            .Select(u => u.GlobalUserId!).ToListAsync(ct);
        return active.ToHashSet(StringComparer.Ordinal);
    }

    private async Task LoadChildrenAsync(IReadOnlyList<TaskInstance> tasks, CancellationToken ct, bool track = false)
    {
        if (tasks.Count == 0) return;
        var urns = tasks.Select(t => t.Urn).ToList();

        var users = await Q(db.TaskRecipientUsers, track).Where(r => urns.Contains(r.TaskUrn)).ToListAsync(ct);
        var groups = await Q(db.TaskRecipientGroups, track).Where(r => urns.Contains(r.TaskUrn)).ToListAsync(ct);
        var attributes = await Q(db.TaskCustomAttributes, track).Where(a => urns.Contains(a.TaskUrn)).ToListAsync(ct);
        var errors = await Q(db.TaskOperationErrors, track).Where(e => urns.Contains(e.TaskUrn)).OrderBy(e => e.Id).ToListAsync(ct);

        var usersBy = users.ToLookup(r => r.TaskUrn);
        var groupsBy = groups.ToLookup(r => r.TaskUrn);
        var attributesBy = attributes.ToLookup(r => r.TaskUrn);
        var errorsBy = errors.ToLookup(r => r.TaskUrn);

        foreach (var task in tasks)
        {
            // For tracked entities EF has already fixed up the collections; for no-tracking queries we attach them here.
            if (!track)
            {
                task.RecipientUsers = [.. usersBy[task.Urn]];
                task.RecipientGroups = [.. groupsBy[task.Urn]];
                task.CustomAttributes = [.. attributesBy[task.Urn]];
                task.OperationErrors = [.. errorsBy[task.Urn]];
            }
        }
    }

    private static IQueryable<T> Q<T>(DbSet<T> set, bool track) where T : class => track ? set : set.AsNoTracking();
}

public sealed class DefinitionRepository(TcpDbContext db)
{
    public async Task<IReadOnlyList<TaskDefinition>> ListAsync(int skip, int top, CancellationToken ct)
    {
        var rows = await db.TaskDefinitions.AsNoTracking().OrderBy(d => d.Urn).Skip(skip).Take(top).ToListAsync(ct);
        return rows.Select(TaskDefinition.FromEntity).ToList();
    }

    public async Task<TaskDefinition?> FindAsync(string urn, CancellationToken ct)
    {
        var row = await db.TaskDefinitions.AsNoTracking().FirstOrDefaultAsync(d => d.Urn == urn, ct);
        return row is null ? null : TaskDefinition.FromEntity(row);
    }

    public async Task<IReadOnlyDictionary<string, TaskDefinition>> FindManyAsync(IEnumerable<string> urns, CancellationToken ct)
    {
        var list = urns.Distinct(StringComparer.Ordinal).ToList();
        var rows = await db.TaskDefinitions.AsNoTracking().Where(d => list.Contains(d.Urn)).ToListAsync(ct);
        return rows.Select(TaskDefinition.FromEntity).ToDictionary(d => d.Urn, StringComparer.Ordinal);
    }

    public async Task<TaskDefinition?> FindByLocalIdAsync(string localId, CancellationToken ct)
    {
        var row = await db.TaskDefinitions.AsNoTracking().FirstOrDefaultAsync(d => d.LocalId == localId, ct);
        return row is null ? null : TaskDefinition.FromEntity(row);
    }
}
