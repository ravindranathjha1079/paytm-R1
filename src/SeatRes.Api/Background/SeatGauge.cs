using Dapper;
using Prometheus;
using SeatRes.Api.Data;
using SeatRes.Api.Domain;
using SeatRes.Api.Observability;

namespace SeatRes.Api.Background;

/// <summary>
/// Sets seatres_seats{show,state} and seatres_payment_pending from the database just before each scrape
/// (at most once a second), using the same effective-status rule as GET /shows/{id}, so they always reconcile.
/// </summary>
public sealed class SeatGauge(Db db, TimeProvider time, ILogger<SeatGauge> log)
{
    private static readonly string[] States = ["available", "held", "confirmed"];
    public const long Never = -1;
    private long _lastRefreshMs = Never;
    private int _registered;

    public void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        Metrics.DefaultRegistry.AddBeforeCollectCallback(async ct =>
        {
            if (!IsStale(Interlocked.Read(ref _lastRefreshMs), Environment.TickCount64)) return;
            await RefreshAsync(ct);
        });
    }

    internal static bool IsStale(long lastRefreshMs, long nowMs) => lastRefreshMs == Never || nowMs - lastRefreshMs >= 1000;

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(1));
            var now = time.GetUtcNow().UtcDateTime;
            var (rows, pending) = await db.Read(async (c, tx) =>
            {
                var counts = (await c.QueryAsync<(string Show, string State, int N)>(new CommandDefinition(
                    $"SELECT show_id::text, {SeatRules.EffectiveStatusSql}, count(*)::int FROM seats GROUP BY 1, 2",
                    new { now }, tx, cancellationToken: cts.Token))).ToList();
                var inFlight = await c.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT count(*) FROM payment_attempts WHERE status IN ('pending', 'unknown')", transaction: tx,
                    cancellationToken: cts.Token));
                return (counts, inFlight);
            }, cts.Token);

            foreach (var show in rows.Select(r => r.Show).Distinct())
                foreach (var state in States)
                    SeatResMetrics.Seats.WithLabels(show, state)
                        .Set(rows.Where(r => r.Show == show && r.State == state).Sum(r => r.N));
            SeatResMetrics.PaymentPending.Set(pending);
            Interlocked.Exchange(ref _lastRefreshMs, Environment.TickCount64);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning("{event} {error}", "seat_gauge.refresh_failed", ex.Message); // keep last values
        }
    }
}
