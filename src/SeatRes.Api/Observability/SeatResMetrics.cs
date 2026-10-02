using Prometheus;
using SeatRes.Api.Domain;

namespace SeatRes.Api.Observability;

public static class SeatResMetrics
{
    public static readonly Counter Confirmed = Metrics.CreateCounter(
        "seatres_reservations_confirmed_total", "Reservations confirmed (paid).", "show");
    public static readonly Counter HoldsCreated = Metrics.CreateCounter(
        "seatres_holds_created_total", "Seat holds created.", "show");
    public static readonly Counter Declined = Metrics.CreateCounter(
        "seatres_reservations_declined_total", "Requests declined, by domain reason.", "reason");
    public static readonly Counter HoldsReclaimed = Metrics.CreateCounter(
        "seatres_holds_reclaimed_total", "Expired holds claimed by another user.", "show");
    public static readonly Counter Payments = Metrics.CreateCounter(
        "seatres_payments_total", "Gateway charge outcomes.", "result");
    public static readonly Counter Refunds = Metrics.CreateCounter(
        "seatres_refunds_total", "Refunds recorded, by reason.", "reason");
    public static readonly Gauge PaymentPending = Metrics.CreateGauge(
        "seatres_payment_pending", "Payment attempts whose outcome is not yet settled.");
    public static readonly Gauge Seats = Metrics.CreateGauge(
        "seatres_seats", "Seats by effective state, read from the database at scrape time.", "show", "state");
    public static readonly Gauge ReconciliationOk = Metrics.CreateGauge(
        "seatres_reconciliation_ok", "1 when the last invariant check passed, 0 when it found a violation.", "check");
    public static readonly Counter FastPathDeclines = Metrics.CreateCounter(
        "seatres_fast_path_declines_total", "Declines answered from memory without a DB transaction.", "layer");
    public static readonly Counter GateTimeouts = Metrics.CreateCounter(
        "seatres_gate_timeouts_total", "Per-seat gate waits that timed out and fell through to the DB.");
    public static readonly Counter DbRetries = Metrics.CreateCounter(
        "seatres_db_retries_total", "Transactions retried after a transient Postgres error.", "sqlstate");
    public static readonly Counter IdempotencyLookups = Metrics.CreateCounter(
        "seatres_idempotency_lookups_total", "Idempotency-key reads against the database before deciding.");
    public static readonly Gauge PaymentOldestPendingSeconds = Metrics.CreateGauge(
        "seatres_payment_oldest_pending_seconds", "Age of the oldest unsettled payment attempt (0 when none).");
    public static readonly Gauge DbUp = Metrics.CreateGauge(
        "seatres_db_up", "1 when the last scrape-time database query succeeded.");
    public static readonly Counter UnhandledErrors = Metrics.CreateCounter(
        "seatres_unhandled_errors_total", "Requests that failed with an unexpected exception (5xx).");

    /// <summary>Publish every known label value at 0 so dashboards and rate() work before the first event.</summary>
    public static void Initialise()
    {
        foreach (var reason in ErrorCodes.DomainDeclines) Declined.WithLabels(reason);
        foreach (var r in new[] { "succeeded", "declined", "unknown" }) Payments.WithLabels(r);
        foreach (var r in new[] { "cancel", "seat_lost", "duplicate_charge" }) Refunds.WithLabels(r);
        foreach (var l in new[] { "taken_cache", "gate" }) FastPathDeclines.WithLabels(l);
        foreach (var c in new[] { "seats", "ledger" }) ReconciliationOk.WithLabels(c).Set(1);
    }
}
