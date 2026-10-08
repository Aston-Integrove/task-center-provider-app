using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Tcp.Infrastructure.Persistence;

/// <summary>Startup helpers for the SQLite database file.</summary>
public static partial class SqliteStore
{
    /// <summary>Creates the folder of a file-based database so the first connection can create the file.</summary>
    public static void EnsureDirectory(string connectionString)
    {
        var source = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(source) || source.StartsWith(':') || source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(source));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    public static async Task SetJournalModeAsync(TcpDbContext db, string mode)
    {
        if (!JournalModePattern().IsMatch(mode))
            throw new InvalidOperationException($"Database:JournalMode '{mode}' is not a valid SQLite journal mode");
        // The mode name is validated above, so it is safe to concatenate (PRAGMA cannot take parameters).
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($"PRAGMA journal_mode = {mode};");
#pragma warning restore EF1002
    }

    [GeneratedRegex("^(DELETE|TRUNCATE|PERSIST|MEMORY|WAL|OFF)$", RegexOptions.IgnoreCase)]
    private static partial Regex JournalModePattern();
}
