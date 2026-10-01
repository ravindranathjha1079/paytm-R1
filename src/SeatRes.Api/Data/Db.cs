using System.Data;
using Dapper;
using Npgsql;
using SeatRes.Api.Observability;

namespace SeatRes.Api.Data;

/// <summary>Short READ COMMITTED transactions with bounded lock waits and one retry on transient contention.</summary>
public sealed class Db(NpgsqlDataSource dataSource)
{
    private static readonly HashSet<string> Retryable = ["40P01", "40001", "55P03"];

    public NpgsqlDataSource DataSource => dataSource;

    public async Task<T> InTx<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            try
            {
                await conn.ExecuteAsync("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '5s'", transaction: tx);
                var result = await work(conn, tx);
                await tx.CommitAsync(ct);
                return result;
            }
            catch (PostgresException e) when (attempt == 1 && Retryable.Contains(e.SqlState))
            {
                SeatResMetrics.DbRetries.WithLabels(e.SqlState).Inc();
            }
        }
    }

    public async Task<T> Read<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var result = await work(conn, tx);
        await tx.CommitAsync(ct);
        return result;
    }
}
