using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Tcp.Infrastructure.Persistence;

namespace Tcp.Infrastructure.Tasks;

/// <summary>
/// FR-SPI-02: <c>{DefinitionLocalId}-{yyyyMMdd}-{seq:D6}</c>, e.g. <c>PR_APPROVAL-20261007-000042</c>. The counter
/// per (definition, day) is incremented atomically in SQL, so concurrent creators never get the same number.
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
                        "INSERT INTO tc.LocalIdSequence (DefinitionLocalId, [Day], [Next]) VALUES (@d, @day, @n); SELECT @n;",
                        definitionLocalId, day, count, ct);
                    last = count;
                }
                catch (SqlException ex) when (ex.Number is 2601 or 2627)
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
            "UPDATE tc.LocalIdSequence SET [Next] = [Next] + @n OUTPUT inserted.[Next] WHERE DefinitionLocalId = @d AND [Day] = @day;",
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
            command.Parameters.Add(new SqlParameter("@d", SqlDbType.VarChar, 64) { Value = definitionLocalId });
            command.Parameters.Add(new SqlParameter("@day", SqlDbType.Date) { Value = day });
            command.Parameters.Add(new SqlParameter("@n", SqlDbType.Int) { Value = count });
            var result = await command.ExecuteScalarAsync(ct);
            return result is null or DBNull ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }
}
