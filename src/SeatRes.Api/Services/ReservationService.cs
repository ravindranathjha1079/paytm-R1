using Microsoft.Extensions.Options;
using SeatRes.Api.Concurrency;
using SeatRes.Api.Data;
using SeatRes.Api.Domain;
using SeatRes.Api.Observability;

namespace SeatRes.Api.Services;

/// <summary>
/// Reserve pipeline: validate → idempotency lookup → [taken-seat cache → per-seat gate] → atomic DB decision
/// → [payment]. The idempotency lookup runs before any in-memory shortcut so a retry of a winning request is
/// replayed rather than mis-declined as "seat taken". The in-memory layers only decline or delay.
/// </summary>
public sealed class ReservationService(
    ShowCatalog catalog,
    ReserveTx reserveTx,
    Db db,
    PaymentService payments,
    TakenSeatCache takenCache,
    SeatGate gate,
    OutcomeRecorder outcomes,
    IOptions<SeatResOptions> options,
    ILogger<ReservationService> log)
{
    public async Task<ApiResult> ReserveAsync(Guid showId, string userId, ReserveRequest req, string? headerKey,
        string? gatewayHint, CancellationToken ct)
    {
        var show = await catalog.GetAsync(showId, ct);
        if (show is null) return ApiResult.Fail(404, ErrorCodes.NotFound, "show not found");

        if (req.UserId is { } claimed && claimed != userId)
            log.LogWarning("{event} {claimed_user_id} {user_id}", "spoof_attempt", claimed, userId);

        var (key, keyError) = IdempotencyKey.Resolve(headerKey, req.IdempotencyKey);
        if (keyError is not null) return keyError;

        var (seatNos, seatError) = ResolveSeats(show, req.Seats);
        if (seatError is not null) return seatError;
        var labels = req.Seats!;
        var confirm = req.Confirm ?? false;
        var hash = RequestHash.ForReserve(show.Id, labels, confirm);

        // A seat known taken by someone else is declined from memory with no database work at all: a winning
        // idempotency key for that seat can only belong to its holder, so this caller has nothing to replay.
        var fast = options.Value.FastPathEnabled;
        var cached = fast ? FastDecline(show, seatNos!, userId) : null;
        if (cached is { CallerMayHoldIt: false })
            return outcomes.Record("reserve", cached.Value.Decline, userId, show.Id, null, labels, layer: "taken_cache");

        // Otherwise exactly one idempotency lookup: a key already used is replayed, never re-decided.
        var existing = await IdempotencyStore.FindAsync(db.DataSource, userId, IdemScope.Reserve, key!, ct);
        if (existing is not null)
            return outcomes.Record("reserve", IdempotencyStore.Replay(existing, hash), userId, show.Id, null, labels);
        if (cached is not null)
            return outcomes.Record("reserve", cached.Value.Decline, userId, show.Id, null, labels, layer: "taken_cache");

        var command = new ReserveCommand(show, userId, key!, hash, seatNos!, confirm, gatewayHint);
        var (result, gated) = await DecideAsync(command, fast, ct);
        if (gated is { CallerMayHoldIt: false })
            return outcomes.Record("reserve", gated.Value.Decline, userId, show.Id, null, labels, layer: "gate");
        if (gated is not null)
        {
            // The cache only learns a seat is taken after the winner commits. If that winner was this same key
            // (a concurrent retry), its idempotency row is now visible: replay it instead of declining.
            var committed = await IdempotencyStore.FindAsync(db.DataSource, userId, IdemScope.Reserve, key!, ct);
            return committed is not null
                ? outcomes.Record("reserve", IdempotencyStore.Replay(committed, hash), userId, show.Id, null, labels)
                : outcomes.Record("reserve", gated.Value.Decline, userId, show.Id, null, labels, layer: "gate");
        }

        // Metrics only after the transaction committed, so a retried transaction is never counted twice.
        if (result!.Response.StatusCode == 201 && !result.Response.Replayed)
            SeatResMetrics.HoldsCreated.WithLabels(show.Id.ToString()).Inc();
        if (result.ReclaimedHolds > 0)
            SeatResMetrics.HoldsReclaimed.WithLabels(show.Id.ToString()).Inc(result.ReclaimedHolds);

        var response = result.Pinned is null ? result.Response : await payments.RunAsync(result.Pinned);
        return outcomes.Record("reserve", response, userId, show.Id, result.ReservationId, labels);
    }

    /// <summary>
    /// Holds the per-seat gate only around the DB decision. Returns a fast decline instead when, after waiting
    /// at the gate, the cache shows the seat was taken by the request ahead — the gate is released before any
    /// further database work so waiters drain without queueing on each other's round trips.
    /// </summary>
    private async Task<(ReserveTxResult? Result, FastDeclineResult? GatedDecline)> DecideAsync(ReserveCommand cmd, bool fast,
        CancellationToken ct)
    {
        using var lease = fast ? await gate.AcquireAsync(cmd.Show.Id, cmd.SeatNos, ct) : SeatGate.Lease.None;
        if (lease.Gated && FastDecline(cmd.Show, cmd.SeatNos, cmd.UserId) is { } gated)
            return (null, gated);

        var result = await reserveTx.ExecuteAsync(cmd, ct);
        if (fast && result.ReservationId is not null)
            takenCache.MarkTaken(cmd.Show.Id, result.ClaimedSeatNos, result.ClaimedUntil, cmd.UserId);
        if (fast)
            foreach (var t in result.TakenSeats)
                takenCache.MarkTaken(cmd.Show.Id, [t.SeatNo], t.Until, t.Holder);
        return (result, null);
    }

    /// <param name="CallerMayHoldIt">True if any taken seat might be the caller's own (or its holder is unknown):
    /// then the caller's idempotency key must be checked before declining.</param>
    private readonly record struct FastDeclineResult(ApiResult Decline, bool CallerMayHoldIt);

    private FastDeclineResult? FastDecline(ShowInfo show, int[] seatNos, string userId)
    {
        var taken = takenCache.FindTakenWithHolders(show.Id, seatNos);
        if (taken.Length == 0) return null;
        var decline = ApiResult.Fail(409, ErrorCodes.SeatTaken, "one or more requested seats are already taken",
            new Dictionary<string, object?> { ["taken"] = taken.Select(t => show.LabelOf(t.SeatNo)).ToArray() });
        return new FastDeclineResult(decline, taken.Any(t => t.Holder is null || t.Holder == userId));
    }

    private (int[]? SeatNos, ApiResult? Error) ResolveSeats(ShowInfo show, string[]? seats)
    {
        var max = options.Value.MaxSeatsPerRequest;
        if (seats is not { Length: > 0 })
            return (null, Bad("seats must contain at least one label"));
        if (seats.Length > max)
            return (null, Bad($"a request may name at most {max} seats"));
        if (seats.Distinct(StringComparer.Ordinal).Count() != seats.Length)
            return (null, Bad("seats must not repeat"));
        var unknown = seats.Where(s => s is null || !show.SeatNoByLabel.ContainsKey(s)).ToArray();
        if (unknown.Length > 0)
            return (null, ApiResult.Fail(400, ErrorCodes.UnknownSeats, "unknown seat labels for this show",
                new Dictionary<string, object?> { ["unknown"] = unknown }));
        return (seats.Select(s => show.SeatNoByLabel[s]).Order().ToArray(), null);
    }

    private static ApiResult Bad(string message) => ApiResult.Fail(400, ErrorCodes.BadRequest, message);
}
