using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tcp.Domain.Identity;
using Tcp.Infrastructure.Persistence;
using Tcp.Infrastructure.Scim.Filter;
using Tcp.Infrastructure.Scim.Patch;

namespace Tcp.Infrastructure.Scim;

/// <summary>Groups: CRUD plus membership PATCH (spec 003). <c>DisplayName</c> is what <c>recipientGroups</c> carries.</summary>
public sealed class ScimGroupService(
    TcpDbContext db,
    IOptions<ScimOptions> options,
    ITaskIdentityReferences taskReferences,
    TimeProvider time)
{
    public async Task<ScimGroup> CreateAsync(JsonObject payload, string clientId, CancellationToken ct)
    {
        var write = ScimGroupMapper.Parse(payload);
        if (await db.ScimGroups.AnyAsync(g => g.DisplayName == write.DisplayName, ct))
            throw ScimException.Conflict($"group displayName '{write.DisplayName}' already exists");

        var members = await ResolveMembersAsync(write.MemberValues, ct);
        var now = ScimDb.TruncateToMs(time.GetUtcNow().UtcDateTime);
        var group = new ScimGroup
        {
            Id = Guid.NewGuid(), DisplayName = write.DisplayName, ExternalId = write.ExternalId,
            Created = now, LastModified = now,
        };
        foreach (var user in members) group.Members.Add(new ScimGroupMember { GroupId = group.Id, UserId = user.Id, User = user });

        db.ScimGroups.Add(group);
        ScimDb.Audit(db, now, clientId, "Create", "Group", group.Id, $"displayName={group.DisplayName} members={members.Count}");
        await SaveAsync(ct);
        return group;
    }

    public Task<ScimGroup?> FindAsync(Guid id, CancellationToken ct) =>
        db.ScimGroups.AsNoTracking().Include(g => g.Members).ThenInclude(m => m.User).FirstOrDefaultAsync(g => g.Id == id, ct);

    public async Task<PagedResult<ScimGroup>> ListAsync(string? filter, int startIndex, int count, bool includeMembers, CancellationToken ct)
    {
        IQueryable<ScimGroup> query = db.ScimGroups.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter))
            query = query.Where(ScimFilterToLinq.ForGroups(ScimFilterParser.Parse(filter)));

        var total = await query.CountAsync(ct);
        if (count <= 0) return new PagedResult<ScimGroup>([], total);

        var page = query.OrderBy(g => g.DisplayName).ThenBy(g => g.Id).Skip(Math.Max(0, startIndex - 1)).Take(count);
        if (includeMembers) page = page.Include(g => g.Members).ThenInclude(m => m.User);
        return new PagedResult<ScimGroup>(await page.ToListAsync(ct), total);
    }

    public Task<ScimGroup> ReplaceAsync(Guid id, JsonObject payload, string clientId, CancellationToken ct) =>
        UpdateAsync(id, payload, clientId, "Replace", ct);

    public async Task<ScimGroup> PatchAsync(Guid id, JsonObject patch, string clientId, CancellationToken ct)
    {
        var group = await FindAsync(id, ct) ?? throw ScimException.NotFound($"Group {id} not found");
        var patched = ScimPatchApplier.Apply(ScimGroupMapper.ToJson(group, string.Empty), patch);
        return await UpdateAsync(id, patched, clientId, "Patch", ct);
    }

    private async Task<ScimGroup> UpdateAsync(Guid id, JsonObject payload, string clientId, string operation, CancellationToken ct)
    {
        var group = await db.ScimGroups.Include(g => g.Members).ThenInclude(m => m.User).FirstOrDefaultAsync(g => g.Id == id, ct)
            ?? throw ScimException.NotFound($"Group {id} not found");

        var write = ScimGroupMapper.Parse(payload);
        if (!string.Equals(write.DisplayName, group.DisplayName, StringComparison.OrdinalIgnoreCase) &&
            await db.ScimGroups.AnyAsync(g => g.DisplayName == write.DisplayName && g.Id != id, ct))
            throw ScimException.Conflict($"group displayName '{write.DisplayName}' already exists");

        var wanted = await ResolveMembersAsync(write.MemberValues, ct);
        var wantedIds = wanted.Select(u => u.Id).ToHashSet();

        foreach (var removed in group.Members.Where(m => !wantedIds.Contains(m.UserId)).ToList())
            group.Members.Remove(removed);
        var existing = group.Members.Select(m => m.UserId).ToHashSet();
        foreach (var user in wanted.Where(u => !existing.Contains(u.Id)))
            group.Members.Add(new ScimGroupMember { GroupId = group.Id, UserId = user.Id, User = user });

        var now = ScimDb.TruncateToMs(time.GetUtcNow().UtcDateTime);
        group.DisplayName = write.DisplayName;
        group.ExternalId = write.ExternalId;
        group.LastModified = now;
        ScimDb.Audit(db, now, clientId, operation, "Group", group.Id, $"displayName={group.DisplayName} members={wanted.Count}");

        await SaveAsync(ct);
        return group;
    }

    public async Task DeleteAsync(Guid id, string clientId, CancellationToken ct)
    {
        var group = await db.ScimGroups.FirstOrDefaultAsync(g => g.Id == id, ct)
            ?? throw ScimException.NotFound($"Group {id} not found");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = ScimDb.TruncateToMs(time.GetUtcNow().UtcDateTime);

        await taskReferences.RemoveGroupAsync(group.DisplayName, now, ct);
        ScimDb.Audit(db, now, clientId, "Delete", "Group", id, $"displayName={group.DisplayName}");
        db.ScimGroups.Remove(group); // memberships cascade
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<List<ScimUser>> ResolveMembersAsync(IReadOnlyList<string> values, CancellationToken ct)
    {
        var users = new List<ScimUser>();
        foreach (var value in values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ScimUser? user = null;
            if (Guid.TryParse(value, out var userId))
                user = await db.ScimUsers.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct);
            if (user is null && options.Value.AcceptGlobalUserIdAsMemberValue && GlobalUserId.Normalize(value) is { } gid)
                user = await db.ScimUsers.FirstOrDefaultAsync(u => u.GlobalUserId == gid && !u.IsDeleted, ct);

            users.Add(user ?? throw ScimException.BadRequest("invalidValue", $"Unknown member '{value}'"));
        }
        return users;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ScimDb.IsUniqueViolation(ex))
        {
            throw ScimException.Conflict("displayName already exists");
        }
    }
}
