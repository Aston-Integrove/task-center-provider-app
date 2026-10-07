using Microsoft.EntityFrameworkCore;
using Tcp.Domain.Identity;

namespace Tcp.Infrastructure.Persistence;

/// <summary>
/// Single EF Core context for the provider. Feature specs add their entity sets via
/// <see cref="IEntityTypeConfiguration{TEntity}"/> classes picked up from this assembly.
/// </summary>
public class TcpDbContext(DbContextOptions<TcpDbContext> options) : DbContext(options)
{
    public DbSet<ScimUser> ScimUsers => Set<ScimUser>();
    public DbSet<ScimGroup> ScimGroups => Set<ScimGroup>();
    public DbSet<ScimGroupMember> ScimGroupMembers => Set<ScimGroupMember>();
    public DbSet<ScimAuditEntry> ScimAudit => Set<ScimAuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TcpDbContext).Assembly);
    }
}
