using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tcp.Infrastructure.Persistence;

/// <summary>Design-time factory for <c>dotnet ef migrations</c>; never used at runtime.</summary>
public sealed class TcpDbContextFactory : IDesignTimeDbContextFactory<TcpDbContext>
{
    public TcpDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Sql")
            ?? @"Server=(localdb)\MSSQLLocalDB;Database=tcp_design;Trusted_Connection=True;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<TcpDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new TcpDbContext(options);
    }
}
