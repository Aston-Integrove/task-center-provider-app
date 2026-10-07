using Microsoft.EntityFrameworkCore;
using Tcp.Domain.Identity;
using Tcp.Domain.Tasks;

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

    public DbSet<TaskDefinitionEntity> TaskDefinitions => Set<TaskDefinitionEntity>();
    public DbSet<TaskInstance> Tasks => Set<TaskInstance>();
    public DbSet<TaskRecipientUser> TaskRecipientUsers => Set<TaskRecipientUser>();
    public DbSet<TaskRecipientGroup> TaskRecipientGroups => Set<TaskRecipientGroup>();
    public DbSet<TaskCustomAttribute> TaskCustomAttributes => Set<TaskCustomAttribute>();
    public DbSet<TaskOperationError> TaskOperationErrors => Set<TaskOperationError>();
    public DbSet<OperationLogEntry> OperationLog => Set<OperationLogEntry>();
    public DbSet<LocalIdSequence> LocalIdSequences => Set<LocalIdSequence>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TcpDbContext).Assembly);
    }
}
