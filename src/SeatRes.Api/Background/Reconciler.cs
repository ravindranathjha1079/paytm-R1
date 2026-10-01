using Dapper;
using SeatRes.Api.Data;
using SeatRes.Api.Observability;

namespace SeatRes.Api.Background;

public sealed record ReconciliationReport(bool SeatsOk, bool LedgerOk, IReadOnlyList<string> Problems);

/// <summary>
/// Independent invariant checks over the whole database, exported as seatres_reconciliation_ok{check}:
///  seats  — every show has exactly total_seats rows; every owned seat points at a reservation of the same user;
///           every confirmed seat belongs to a confirmed reservation.
///  ledger — for every reservation with no payment in flight, charges − refunds == (confirmed ? amount : 0).
/// </summary>
public sealed class Reconciler(Db db, ILogger<Reconciler> log)
{
    public async Task<ReconciliationReport> RunOnceAsync(CancellationToken ct)
    {
        var problems = new List<string>();
        var (seatProblems, ledgerProblems) = await db.Read(async (c, tx) =>
        {
            var seats = new List<string>();
            seats.AddRange((await c.QueryAsync<Guid>(
                """
                SELECT s.id FROM shows s LEFT JOIN seats t ON t.show_id = s.id
                GROUP BY s.id, s.total_seats HAVING count(t.seat_no) <> s.total_seats LIMIT 10
                """, transaction: tx)).Select(id => $"show {id}: seat rows != total_seats"));
            var orphans = await c.ExecuteScalarAsync<long>(
                """
                SELECT count(*) FROM seats s LEFT JOIN reservations r ON r.id = s.reservation_id
                WHERE s.reservation_id IS NOT NULL
                  AND (r.id IS NULL OR r.user_id <> s.holder_user_id OR NOT (s.seat_no = ANY (r.seat_nos)))
                """, transaction: tx);
            if (orphans > 0) seats.Add($"{orphans} owned seats do not match their reservation");
            var badConfirmed = await c.ExecuteScalarAsync<long>(
                """
                SELECT count(*) FROM seats s JOIN reservations r ON r.id = s.reservation_id
                WHERE s.status = 'confirmed' AND r.status <> 'confirmed'
                """, transaction: tx);
            if (badConfirmed > 0) seats.Add($"{badConfirmed} confirmed seats belong to unconfirmed reservations");

            var ledger = (await c.QueryAsync<Guid>(
                """
                SELECT r.id FROM reservations r
                LEFT JOIN payments p ON p.reservation_id = r.id
                WHERE NOT EXISTS (SELECT 1 FROM payment_attempts a
                                  WHERE a.reservation_id = r.id AND a.status IN ('pending', 'unknown', 'refund_pending'))
                GROUP BY r.id, r.status, r.amount_paise
                HAVING coalesce(sum(CASE WHEN p.kind = 'charge' THEN p.amount_paise WHEN p.kind = 'refund' THEN -p.amount_paise END), 0)
                       <> CASE WHEN r.status = 'confirmed' THEN r.amount_paise ELSE 0 END
                LIMIT 10
                """, transaction: tx)).Select(id => $"reservation {id}: ledger does not match status").ToList();
            return (seats, ledger);
        }, ct);

        problems.AddRange(seatProblems);
        problems.AddRange(ledgerProblems);
        SeatResMetrics.ReconciliationOk.WithLabels("seats").Set(seatProblems.Count == 0 ? 1 : 0);
        SeatResMetrics.ReconciliationOk.WithLabels("ledger").Set(ledgerProblems.Count == 0 ? 1 : 0);
        foreach (var p in problems) log.LogError("{event} {problem}", "reconciliation.violation", p);
        return new ReconciliationReport(seatProblems.Count == 0, ledgerProblems.Count == 0, problems);
    }
}
