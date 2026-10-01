using Microsoft.Extensions.Options;
using SeatRes.Api.Data;
using SeatRes.Api.Domain;
using SeatRes.Api.Observability;

namespace SeatRes.Api.Services;

/// <summary>
/// Reserve pipeline: validate → idempotency lookup → [fast path] → atomic DB decision → [payment].
/// The idempotency lookup runs before any in-memory shortcut so a retry of a winning request is
/// replayed rather than mis-declined as "seat taken".
/// </summary>
public sealed class ReservationService(
    ShowCatalog catalog,
    ReserveTx reserveTx,
    Db db,
    PaymentService payments,
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

        var result = await reserveTx.ExecuteAsync(new ReserveCommand(show, userId, key!, hash, seatNos!, confirm, gatewayHint), ct);
        if (result.Response.StatusCode == 201 && !result.Response.Replayed)
            SeatResMetrics.HoldsCreated.WithLabels(show.Id.ToString()).Inc();

        var response = result.Pinned is null ? result.Response : await payments.RunAsync(result.Pinned);
        return outcomes.Record("reserve", response, userId, show.Id, result.ReservationId, labels);
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
