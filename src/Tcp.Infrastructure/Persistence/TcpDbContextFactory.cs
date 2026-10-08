using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tcp.Infrastructure.Persistence;

/// <summary>Design-time factory for <c>dotnet ef migrations</c>; never used at runtime.</summary>
public sealed class TcpDbContextFactory : IDesignTimeDbContextFactory<TcpDbContext>
{
    public TcpDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Database")
            ?? "Data Source=tcp_design.db";
        var options = new DbContextOptionsBuilder<TcpDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new TcpDbContext(options);
    }
}
