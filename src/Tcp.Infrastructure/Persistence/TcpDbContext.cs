using Microsoft.EntityFrameworkCore;

namespace Tcp.Infrastructure.Persistence;

/// <summary>
/// Single EF Core context for the provider. Feature specs add their entity sets via
/// <see cref="IEntityTypeConfiguration{TEntity}"/> classes picked up from this assembly.
/// </summary>
public class TcpDbContext(DbContextOptions<TcpDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TcpDbContext).Assembly);
    }
}
