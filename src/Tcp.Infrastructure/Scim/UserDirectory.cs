using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tcp.Domain.Identity;
using Tcp.Infrastructure.Persistence;

namespace Tcp.Infrastructure.Scim;

/// <summary><see cref="IUserDirectory"/> over the SCIM store (FR-SCIM-09). Only non-deleted users are visible.</summary>
public sealed class UserDirectory(TcpDbContext db) : IUserDirectory
{
    public async Task<DirectoryUser?> FindByGlobalUserIdAsync(string globalUserId, CancellationToken ct = default)
    {
        var normalized = GlobalUserId.Normalize(globalUserId);
        if (normalized is null) return null;
        var user = await db.ScimUsers.AsNoTracking().FirstOrDefaultAsync(u => u.GlobalUserId == normalized && !u.IsDeleted, ct);
        return user is null ? null : ToDirectoryUser(user);
    }

    public async Task<DirectoryUser?> FindUniqueByEmailAsync(string email, CancellationToken ct = default)
    {
        // PrimaryEmail uses a case-insensitive collation; two matches means ambiguous, so no match.
        var matches = await db.ScimUsers.AsNoTracking()
            .Where(u => !u.IsDeleted && u.GlobalUserId != null && u.PrimaryEmail == email)
            .Take(2).ToListAsync(ct);
        return matches.Count == 1 ? ToDirectoryUser(matches[0]) : null;
    }

    public async Task<bool> IsMemberOfAnyGroupAsync(string globalUserId, IReadOnlyCollection<string> groupNames, CancellationToken ct = default)
    {
        var normalized = GlobalUserId.Normalize(globalUserId);
        if (normalized is null || groupNames.Count == 0) return false;
        var names = groupNames.ToList();
        return await db.ScimGroupMembers.AsNoTracking()
            .AnyAsync(m => m.User.GlobalUserId == normalized && !m.User.IsDeleted && names.Contains(m.Group.DisplayName), ct);
    }

    public async Task<IReadOnlyList<DirectoryUser>> ListActiveAsync(CancellationToken ct = default)
    {
        var users = await db.ScimUsers.AsNoTracking()
            .Where(u => u.Active && !u.IsDeleted && u.GlobalUserId != null)
            .OrderBy(u => u.UserName).ToListAsync(ct);
        return users.Select(ToDirectoryUser).ToList();
    }

    private static DirectoryUser ToDirectoryUser(ScimUser u) =>
        new(u.GlobalUserId!, u.UserName, u.PrimaryEmail, u.DisplayName ?? u.UserName, u.Active);
}

public static class ScimServiceCollectionExtensions
{
    /// <summary>Registers the SCIM services and replaces the empty directory with the database-backed one.</summary>
    public static IServiceCollection AddScimInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ITaskIdentityReferences, NoTaskIdentityReferences>();
        services.AddScoped<ScimUserService>();
        services.AddScoped<ScimGroupService>();
        services.RemoveAll<IUserDirectory>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        return services;
    }
}
