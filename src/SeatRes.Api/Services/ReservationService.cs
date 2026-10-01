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

        var existing = await IdempotencyStore.FindAsync(db.DataSource, userId, IdemScope.Reserve, key!, ct);
        if (existing is not null)
            return outcomes.Record("reserve", IdempotencyStore.Replay(existing, hash), userId, show.Id, null, labels);

        var fast = options.Value.FastPathEnabled;
        if (fast && FastDecline(show, seatNos!) is { } cached)
            return outcomes.Record("reserve", cached, userId, show.Id, null, labels, layer: "taken_cache");

        ReserveTxResult result;
        using (var lease = fast ? await gate.AcquireAsync(show.Id, seatNos!, ct) : SeatGate.Lease.None)
        {
            if (lease.Gated && FastDecline(show, seatNos!) is { } gated)
                return outcomes.Record("reserve", gated, userId, show.Id, null, labels, layer: "gate");

            result = await reserveTx.ExecuteAsync(new ReserveCommand(show, userId, key!, hash, seatNos!, confirm, gatewayHint), ct);

            if (fast && result.ReservationId is not null)
                takenCache.MarkTaken(show.Id, result.ClaimedSeatNos, result.ClaimedUntil);
            if (fast)
                foreach (var t in result.TakenSeats)
                    takenCache.MarkTaken(show.Id, [t.SeatNo], t.Until);
        }

        if (result.Response.StatusCode == 201 && !result.Response.Replayed)
            SeatResMetrics.HoldsCreated.WithLabels(show.Id.ToString()).Inc();

        var response = result.Pinned is null ? result.Response : await payments.RunAsync(result.Pinned);
        return outcomes.Record("reserve", response, userId, show.Id, result.ReservationId, labels);
    }

    private ApiResult? FastDecline(ShowInfo show, int[] seatNos)
    {
        var taken = takenCache.FindTaken(show.Id, seatNos);
        return taken.Length == 0
            ? null
            : ApiResult.Fail(409, ErrorCodes.SeatTaken, "one or more requested seats are already taken",
                new Dictionary<string, object?> { ["taken"] = taken.Select(show.LabelOf).ToArray() });
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
