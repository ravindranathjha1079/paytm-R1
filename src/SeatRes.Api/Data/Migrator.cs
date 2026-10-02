using Dapper;
using Npgsql;

namespace SeatRes.Api.Data;

public sealed class MigrationState
{
    private volatile bool _applied;
    public bool Applied { get => _applied; set => _applied = value; }
}

/// <summary>Applies embedded Data/Migrations/*.sql in name order, once, under an advisory lock.</summary>
public sealed class Migrator(NpgsqlDataSource dataSource, ILogger<Migrator> log)
{
    private const long LockId = 7_272_741;

    public async Task ApplyAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(@LockId)", new { LockId }, tx);
        await conn.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())",
            transaction: tx);
        var applied = (await conn.QueryAsync<string>("SELECT name FROM schema_migrations", transaction: tx)).ToHashSet();

        foreach (var (name, sql) in Scripts())
        {
            if (applied.Contains(name)) continue;
            await conn.ExecuteAsync(sql, transaction: tx);
            await conn.ExecuteAsync("INSERT INTO schema_migrations (name) VALUES (@name)", new { name }, tx);
            log.LogInformation("migration_applied {migration}", name);
        }

        await tx.CommitAsync(ct);
    }

    private static IEnumerable<(string Name, string Sql)> Scripts()
    {
        var asm = typeof(Migrator).Assembly;
        foreach (var resource in asm.GetManifestResourceNames().Where(n => n.EndsWith(".sql")).Order(StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var name = resource[(resource.IndexOf("Migrations.", StringComparison.Ordinal) + "Migrations.".Length)..];
            yield return (name, reader.ReadToEnd());
        }
    }
}

/// <summary>Keeps retrying migrations until the database is reachable, so a cold start in any order comes up healthy.</summary>
public sealed class MigrationHostedService(
    Migrator migrator, MigrationState state, Concurrency.SeenKeys seenKeys, NpgsqlDataSource dataSource, TimeProvider time,
    ILogger<MigrationHostedService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await migrator.ApplyAsync(stoppingToken);
                // Readiness waits for this too: the memory-only decline is only exact once recent keys are known.
                var loaded = await seenKeys.LoadAsync(dataSource, time.GetUtcNow().UtcDateTime - Concurrency.SeenKeys.Window, stoppingToken);
                state.Applied = true;
                log.LogInformation("migrations_ready {seen_keys_loaded}", loaded);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("migrations_waiting_for_db {error}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
