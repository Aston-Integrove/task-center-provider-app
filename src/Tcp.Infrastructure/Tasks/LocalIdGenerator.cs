using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Tcp.Infrastructure.Persistence;

namespace Tcp.Infrastructure.Tasks;

/// <summary>
/// FR-SPI-02: <c>{DefinitionLocalId}-{yyyyMMdd}-{seq:D6}</c>, e.g. <c>PR_APPROVAL-20261007-000042</c>. The counter
/// per (definition, day) is incremented atomically in SQL (one UPDATE ... RETURNING statement; SQLite allows a single
/// writer at a time), so concurrent creators never get the same number.
/// </summary>
public sealed class LocalIdGenerator(TcpDbContext db, TimeProvider time)
{
    public async Task<string> NextAsync(string definitionLocalId, CancellationToken ct) =>
        (await NextBlockAsync(definitionLocalId, 1, ct))[0];

    /// <summary>Reserves <paramref name="count"/> consecutive ids in one round trip (bulk generator).</summary>
    public async Task<IReadOnlyList<string>> NextBlockAsync(string definitionLocalId, int count, CancellationToken ct)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var day = time.GetUtcNow().UtcDateTime.Date;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var last = await IncrementAsync(definitionLocalId, day, count, ct);
            if (last is null)
            {
                try
                {
                    await ExecuteAsync(
                        "INSERT INTO \"LocalIdSequence\" (\"DefinitionLocalId\", \"Day\", \"Next\") VALUES ($d, $day, $n) RETURNING \"Next\";",
                        definitionLocalId, day, count, ct);
                    last = count;
                }
                catch (SqliteException ex) when (ex.SqliteExtendedErrorCode is 2067 or 1555) // UNIQUE / PRIMARYKEY
                {
                    continue; // somebody else created today's row first -> increment it
                }
            }
            return Enumerable.Range(last.Value - count + 1, count)
                .Select(n => string.Create(CultureInfo.InvariantCulture, $"{definitionLocalId}-{day:yyyyMMdd}-{n:D6}")).ToList();
        }
        throw new InvalidOperationException("Could not allocate task local ids");
    }

    private Task<int?> IncrementAsync(string definitionLocalId, DateTime day, int count, CancellationToken ct) =>
        ExecuteAsync(
            "UPDATE \"LocalIdSequence\" SET \"Next\" = \"Next\" + $n WHERE \"DefinitionLocalId\" = $d AND \"Day\" = $day RETURNING \"Next\";",
            definitionLocalId, day, count, ct);

    private async Task<int?> ExecuteAsync(string sql, string definitionLocalId, DateTime day, int count, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.Parameters.Add(new SqliteParameter("$d", definitionLocalId));
            command.Parameters.Add(new SqliteParameter("$day", day));
            command.Parameters.Add(new SqliteParameter("$n", count));
            var result = await command.ExecuteScalarAsync(ct);
            return result is null or DBNull ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }
}
