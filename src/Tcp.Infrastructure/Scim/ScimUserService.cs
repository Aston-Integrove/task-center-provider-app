using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tcp.Domain.Identity;
using Tcp.Infrastructure.Persistence;
using Tcp.Infrastructure.Scim.Filter;
using Tcp.Infrastructure.Scim.Patch;

namespace Tcp.Infrastructure.Scim;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);

internal static class ScimDb
{
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 or 1555 }; // UNIQUE / PRIMARYKEY

    public static DateTime TruncateToMs(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    public static void Audit(TcpDbContext db, DateTime at, string clientId, string operation, string resourceType, Guid resourceId, string summary) =>
        db.ScimAudit.Add(new ScimAuditEntry
        {
            At = at,
            ClientId = clientId.Length > 64 ? clientId[..64] : clientId,
            Operation = operation,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Summary = summary.Length > 400 ? summary[..400] : summary,
        });
}

/// <summary>Users: create, read, list, replace, patch, delete (with GDPR anonymisation) per spec 003.</summary>
public sealed class ScimUserService(
    TcpDbContext db,
    IOptions<ScimOptions> options,
    ITaskIdentityReferences taskReferences,
    TimeProvider time)
{
    private GlobalUserIdPolicy Policy => GlobalUserIdPolicy.Parse(options.Value.GlobalUserIdSources);

    public async Task<ScimUser> CreateAsync(JsonObject payload, string clientId, CancellationToken ct)
    {
        var write = ScimUserMapper.Parse(payload);
        var globalUserId = Policy.Derive(write.UserUuid, write.ExternalId, write.PayloadId);
        if (globalUserId is null && options.Value.RequireGlobalUserId)
            throw ScimException.BadRequest("invalidValue", "Global User ID missing (provide the SAP extension userUuid or a GUID externalId)");

        if (await db.ScimUsers.AnyAsync(u => u.UserName == write.UserName, ct))
            throw ScimException.Conflict($"userName '{write.UserName}' already exists");
        if (globalUserId is not null && await db.ScimUsers.AnyAsync(u => u.GlobalUserId == globalUserId, ct))
            throw ScimException.Conflict("The Global User ID is already assigned to another user");

        var now = ScimDb.TruncateToMs(time.GetUtcNow().UtcDateTime);
        var user = new ScimUser
        {
            Id = options.Value.UseGlobalUserIdAsId && globalUserId is not null ? Guid.Parse(globalUserId) : Guid.NewGuid(),
            Created = now,
        };
        Apply(user, write, globalUserId, now);
        db.ScimUsers.Add(user);
        ScimDb.Audit(db, now, clientId, "Create", "User", user.Id, $"userName={user.UserName}");

        await SaveAsync(ct);
        return user;
    }

    public Task<ScimUser?> FindAsync(Guid id, CancellationToken ct) =>
        db.ScimUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);

    public async Task<PagedResult<ScimUser>> ListAsync(string? filter, int startIndex, int count, CancellationToken ct)
    {
        IQueryable<ScimUser> query = db.ScimUsers.AsNoTracking().Where(u => !u.IsDeleted);
        if (!string.IsNullOrWhiteSpace(filter))
            query = query.Where(ScimFilterToLinq.ForUsers(ScimFilterParser.Parse(filter)));

        var total = await query.CountAsync(ct);
        if (count <= 0) return new PagedResult<ScimUser>([], total);

        var items = await query.OrderBy(u => u.UserName).ThenBy(u => u.Id)
            .Skip(Math.Max(0, startIndex - 1)).Take(count).ToListAsync(ct);
        return new PagedResult<ScimUser>(items, total);
    }

    public async Task<ScimUser> ReplaceAsync(Guid id, JsonObject payload, string clientId, CancellationToken ct) =>
        await UpdateAsync(id, payload, clientId, "Replace", ct);

    public async Task<ScimUser> PatchAsync(Guid id, JsonObject patch, string clientId, CancellationToken ct)
    {
        var user = await db.ScimUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct)
            ?? throw ScimException.NotFound($"User {id} not found");
        var patched = ScimPatchApplier.Apply(ScimUserMapper.ToJson(user, string.Empty), patch);
        return await UpdateAsync(id, patched, clientId, "Patch", ct);
    }

    private async Task<ScimUser> UpdateAsync(Guid id, JsonObject payload, string clientId, string operation, CancellationToken ct)
    {
        var user = await db.ScimUsers.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct)
            ?? throw ScimException.NotFound($"User {id} not found");

        var write = ScimUserMapper.Parse(payload);
        var derived = Policy.Derive(write.UserUuid, write.ExternalId, write.PayloadId);
        var globalUserId = user.GlobalUserId;

        if (derived is not null && derived != user.GlobalUserId)
        {
            if (user.GlobalUserId is not null && await taskReferences.IsUserReferencedAsync(user.GlobalUserId, ct))
                throw ScimException.BadRequest("mutability", "The Global User ID cannot change because tasks reference this user");
            if (await db.ScimUsers.AnyAsync(u => u.GlobalUserId == derived && u.Id != id, ct))
                throw ScimException.Conflict("The Global User ID is already assigned to another user");
            globalUserId = derived;
        }
        else if (user.GlobalUserId is null && derived is null && options.Value.RequireGlobalUserId)
        {
            throw ScimException.BadRequest("invalidValue", "Global User ID missing");
        }

        if (!string.Equals(write.UserName, user.UserName, StringComparison.OrdinalIgnoreCase) &&
            await db.ScimUsers.AnyAsync(u => u.UserName == write.UserName && u.Id != id, ct))
            throw ScimException.Conflict($"userName '{write.UserName}' already exists");

        var now = ScimDb.TruncateToMs(time.GetUtcNow().UtcDateTime);
        Apply(user, write, globalUserId, now);
        ScimDb.Audit(db, now, clientId, operation, "User", user.Id, $"userName={user.UserName} active={user.Active}");

        await SaveAsync(ct);
        return user;
    }

    /// <summary>GDPR delete (FR-SCIM-12): one transaction across the idm and task data.</summary>
    public async Task DeleteAsync(Guid id, string clientId, CancellationToken ct)
    {
        var user = await db.ScimUsers.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct)
            ?? throw ScimException.NotFound($"User {id} not found");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = ScimDb.TruncateToMs(time.GetUtcNow().UtcDateTime);

        if (user.GlobalUserId is not null)
            await taskReferences.AnonymiseUserAsync(user.GlobalUserId, now, ct);

        var groupIds = await db.ScimGroupMembers.Where(m => m.UserId == id).Select(m => m.GroupId).ToListAsync(ct);
        await db.ScimGroupMembers.Where(m => m.UserId == id).ExecuteDeleteAsync(ct);
        if (groupIds.Count > 0)
            await db.ScimGroups.Where(g => groupIds.Contains(g.Id)).ExecuteUpdateAsync(s => s.SetProperty(g => g.LastModified, now), ct);

        user.UserName = $"deleted-{id:N}";
        user.ExternalId = null;
        user.GlobalUserId = null;
        user.DisplayName = null;
        user.GivenName = null;
        user.FamilyName = null;
        user.PrimaryEmail = null;
        user.EmailsJson = "[]";
        user.EmailsSearch = "|";
        user.RawJson = "{}";
        user.Active = false;
        user.IsDeleted = true;
        user.LastModified = now;
        ScimDb.Audit(db, now, clientId, "Delete", "User", id, "gdpr-erase");

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static void Apply(ScimUser user, UserWrite write, string? globalUserId, DateTime now)
    {
        user.UserName = write.UserName;
        user.ExternalId = write.ExternalId;
        user.GlobalUserId = globalUserId;
        user.DisplayName = write.DisplayName;
        user.GivenName = write.GivenName;
        user.FamilyName = write.FamilyName;
        user.PrimaryEmail = write.PrimaryEmail;
        user.EmailsJson = write.EmailsJson;
        user.EmailsSearch = write.EmailsSearch;
        user.Active = write.Active;
        user.RawJson = write.RawJson;
        user.LastModified = now;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ScimDb.IsUniqueViolation(ex))
        {
            throw ScimException.Conflict("userName or Global User ID already exists");
        }
    }
}
