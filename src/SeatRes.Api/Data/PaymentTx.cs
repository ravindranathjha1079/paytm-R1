using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SeatRes.Api.Domain;
using SeatRes.Api.Observability;
using SeatRes.Api.Payments;

namespace SeatRes.Api.Data;

public sealed record AttemptRow
{
    public Guid Id { get; init; }
    public Guid ReservationId { get; init; }
    public string GatewayKey { get; init; } = "";
    public string IdemUser { get; init; } = "";
    public string IdemScope { get; init; } = "";
    public string IdemKey { get; init; } = "";
    public long AmountPaise { get; init; }
    public string Status { get; init; } = "";
    public string? RefundReason { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record PinResult(ApiResult? Response, PinnedPayment? Pinned, Guid? ShowId);

public sealed record FinalizeResult(ApiResult Response, Guid? RefundAttemptId, Guid ShowId);

/// <summary>
/// Payment saga, as database steps:
///   Pin      — seats move to payment_pending with a deadline; an attempt row is created (one in flight max).
///   Finalize — after the gateway answers: confirm, keep pinned for a retry, or compensate with a refund.
///   Refunded — record the refund once the gateway confirms it.
/// Money never moves inside a transaction; every gateway call is keyed by the attempt's gateway_key.
/// </summary>
public sealed class PaymentTx(Db db, TimeProvider time, IOptions<SeatResOptions> options)
{
    public const string AttemptColumns =
        "id AS Id, reservation_id AS ReservationId, gateway_key AS GatewayKey, idem_user AS IdemUser, " +
        "idem_scope AS IdemScope, idem_key AS IdemKey, amount_paise AS AmountPaise, status AS Status, " +
        "refund_reason AS RefundReason, created_at AS CreatedAt";

    public Task<PinResult> PinAsync(Guid reservationId, string caller, string key, byte[] hash, string? hint, CancellationToken ct) =>
        db.InTx(async (c, tx) =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            var head = await ReservationLocks.HeadAsync(c, tx, reservationId);
            if (head is null || head.UserId != caller)
                return new PinResult(ApiResult.Fail(404, ErrorCodes.NotFound, "reservation not found"), null, null);

            if (!await IdempotencyStore.TryInsertAsync(c, tx, caller, IdemScope.Confirm, key, hash, now))
            {
                var existing = await IdempotencyStore.GetAsync(c, tx, caller, IdemScope.Confirm, key);
                return new PinResult(IdempotencyStore.Replay(existing!, hash), null, head.ShowId);
            }

            var locked = await ReservationLocks.LockAsync(c, tx, head);
            var res = locked.Reservation;
            var final = await PrecheckAsync(c, tx, locked, now);
            if (final is not null)
            {
                await IdempotencyStore.StoreAsync(c, tx, caller, IdemScope.Confirm, key, final, res.Id);
                return new PinResult(final, null, res.ShowId);
            }

            var deadline = SeatRules.PayDeadline(res.Status, res.HoldExpiresAt, res.PayDeadline, now, options.Value.PayGrace);
            var pinned = await CreatePinAsync(c, tx, res, caller, IdemScope.Confirm, key, deadline, now, hint);
            return new PinResult(null, pinned, res.ShowId);
        }, ct);

    /// <summary>Returns the final answer when the reservation cannot be paid for right now; null when it may be pinned.</summary>
    private static async Task<ApiResult?> PrecheckAsync(NpgsqlConnection c, NpgsqlTransaction tx, LockedReservation locked, DateTime now)
    {
        var res = locked.Reservation;
        switch (res.Status)
        {
            case ReservationStatus.Confirmed:
                return ApiResult.Ok(ReservationStore.ToDto(res, now) with { ExpiresAt = null, PayDeadline = null });
            case ReservationStatus.Expired:
                return HoldLost();
            case ReservationStatus.Cancelled or ReservationStatus.Refunded or ReservationStatus.RefundPending:
                return ApiResult.Fail(409, ErrorCodes.ReservationClosed, $"reservation is {res.Status}");
        }

        if (!locked.OwnsAllSeats)
        {
            await c.ExecuteAsync("UPDATE reservations SET status = 'expired', updated_at = @now WHERE id = @id",
                new { id = res.Id, now }, tx);
            return HoldLost();
        }

        var inFlight = await c.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM payment_attempts WHERE reservation_id = @id AND status IN ('pending', 'unknown'))",
            new { id = res.Id }, tx);
        if (inFlight)
            return ApiResult.Fail(409, ErrorCodes.PaymentInProgress, "a payment for this reservation is already being processed");

        var stillProtected = res.Status == ReservationStatus.Held ? res.HoldExpiresAt > now : res.PayDeadline > now;
        if (!stillProtected)
        {
            // An expired hold stopped counting towards the limit; re-claiming it must fit again.
            var limit = await c.ExecuteScalarAsync<int>("SELECT per_user_limit FROM shows WHERE id = @id", new { id = res.ShowId }, tx);
            var live = await c.ExecuteScalarAsync<int>(
                $"SELECT count(*) FROM seats WHERE show_id = @showId AND holder_user_id = @userId AND reservation_id <> @id AND NOT {SeatRules.TakeableSql}",
                new { showId = res.ShowId, userId = res.UserId, id = res.Id, now }, tx);
            if (live + res.SeatNos.Length > limit)
                return ApiResult.Fail(409, ErrorCodes.PerUserLimit, $"a user may hold at most {limit} seats for this show",
                    new Dictionary<string, object?> { ["limit"] = limit, ["already_held"] = live });
        }
        return null;
    }

    /// <summary>Moves a reservation's seats to payment_pending and opens the single in-flight attempt.</summary>
    internal static async Task<PinnedPayment> CreatePinAsync(NpgsqlConnection c, NpgsqlTransaction tx, ReservationRow res,
        string idemUser, string idemScope, string idemKey, DateTime deadline, DateTime now, string? hint)
    {
        await c.ExecuteAsync(
            "UPDATE seats SET status = 'payment_pending', pay_deadline = @deadline WHERE show_id = @showId AND reservation_id = @id",
            new { deadline, showId = res.ShowId, id = res.Id }, tx);
        await c.ExecuteAsync(
            "UPDATE reservations SET status = 'payment_pending', pay_deadline = @deadline, updated_at = @now WHERE id = @id",
            new { deadline, now, id = res.Id }, tx);
        var attemptNo = await c.ExecuteScalarAsync<int>(
            "SELECT coalesce(max(attempt_no), 0) + 1 FROM payment_attempts WHERE reservation_id = @id", new { id = res.Id }, tx);
        var attemptId = Guid.CreateVersion7();
        var gatewayKey = $"{res.Id:N}-{attemptNo}";
        await c.ExecuteAsync(
            """
            INSERT INTO payment_attempts (id, reservation_id, attempt_no, gateway_key, idem_user, idem_scope, idem_key,
                                          amount_paise, status, created_at, updated_at)
            VALUES (@attemptId, @id, @attemptNo, @gatewayKey, @idemUser, @idemScope, @idemKey, @amount, 'pending', @now, @now)
            """,
            new { attemptId, id = res.Id, attemptNo, gatewayKey, idemUser, idemScope, idemKey, amount = res.AmountPaise, now }, tx);
        return new PinnedPayment(attemptId, gatewayKey, res.AmountPaise, hint);
    }

    public Task<FinalizeResult> FinalizeAsync(Guid attemptId, GatewayStatus outcome, CancellationToken ct) => db.InTx(async (c, tx) =>
    {
        var now = time.GetUtcNow().UtcDateTime;
        var attempt = await c.QuerySingleAsync<AttemptRow>($"SELECT {AttemptColumns} FROM payment_attempts WHERE id = @attemptId",
            new { attemptId }, tx);
        var head = (await ReservationLocks.HeadAsync(c, tx, attempt.ReservationId))!;
        var locked = await ReservationLocks.LockAsync(c, tx, head);
        attempt = await c.QuerySingleAsync<AttemptRow>($"SELECT {AttemptColumns} FROM payment_attempts WHERE id = @attemptId FOR UPDATE",
            new { attemptId }, tx);
        var res = locked.Reservation;

        if (attempt.Status is not ("pending" or "unknown"))
        {
            // Already finalized (by the live request or by recovery): answer what was stored.
            var stored = await IdempotencyStore.GetAsync(c, tx, attempt.IdemUser, attempt.IdemScope, attempt.IdemKey);
            return new FinalizeResult(stored?.ResponseStatus is { } ? IdempotencyStore.Replay(stored, stored.RequestHash) with { Replayed = false }
                : IdempotencyStore.Processing(), null, res.ShowId);
        }

        var fromReserve = attempt.IdemScope == IdemScope.Reserve;
        ApiResult response;
        Guid? refund = null;

        switch (outcome)
        {
            case GatewayStatus.Succeeded:
                await RecordChargeAsync(c, tx, attempt, now);
                if (res.Status == ReservationStatus.Confirmed)
                {
                    await SetAttemptAsync(c, tx, attempt.Id, "refund_pending", now, "duplicate_charge");
                    refund = attempt.Id;
                    response = ApiResult.Ok(ConfirmedDto(res, now));
                }
                else if (res.Status == ReservationStatus.PaymentPending && locked.OwnsAllSeats)
                {
                    await c.ExecuteAsync(
                        "UPDATE seats SET status = 'confirmed', hold_expires_at = NULL, pay_deadline = NULL WHERE show_id = @showId AND reservation_id = @id",
                        new { showId = res.ShowId, id = res.Id }, tx);
                    await c.ExecuteAsync(
                        "UPDATE reservations SET status = 'confirmed', hold_expires_at = NULL, pay_deadline = NULL, updated_at = @now WHERE id = @id",
                        new { id = res.Id, now }, tx);
                    await SetAttemptAsync(c, tx, attempt.Id, "succeeded", now);
                    SeatResMetrics.Confirmed.WithLabels(res.ShowId.ToString()).Inc();
                    response = ApiResult.Ok(ConfirmedDto(res, now), fromReserve ? 201 : 200);
                }
                else
                {
                    // Paid, but the pin lapsed and someone else claimed a seat: compensate.
                    await ReleaseOwnedSeatsAsync(c, tx, res);
                    await c.ExecuteAsync("UPDATE reservations SET status = 'refund_pending', updated_at = @now WHERE id = @id",
                        new { id = res.Id, now }, tx);
                    await SetAttemptAsync(c, tx, attempt.Id, "refund_pending", now, "seat_lost");
                    refund = attempt.Id;
                    response = HoldLost(refunded: true);
                }
                break;

            case GatewayStatus.Declined or GatewayStatus.NotFound:
                await SetAttemptAsync(c, tx, attempt.Id, "declined", now);
                if (fromReserve)
                {
                    // Hold-and-pay in one call: no hold to fall back to, so the seats go straight back.
                    await ReleaseOwnedSeatsAsync(c, tx, res);
                    await c.ExecuteAsync("UPDATE reservations SET status = 'cancelled', updated_at = @now WHERE id = @id",
                        new { id = res.Id, now }, tx);
                    response = ApiResult.Fail(402, ErrorCodes.PaymentDeclined, "payment was declined; the seats were released");
                }
                else
                {
                    response = ApiResult.Fail(402, ErrorCodes.PaymentDeclined,
                        "payment was declined; the seats stay reserved for you until retry_until",
                        new Dictionary<string, object?> { ["retry_until"] = res.PayDeadline });
                }
                break;

            default: // Unknown: leave the pin in place; recovery asks the gateway later.
                await SetAttemptAsync(c, tx, attempt.Id, "unknown", now);
                return new FinalizeResult(IdempotencyStore.Processing(), null, res.ShowId);
        }

        await IdempotencyStore.StoreAsync(c, tx, attempt.IdemUser, attempt.IdemScope, attempt.IdemKey, response, res.Id);
        return new FinalizeResult(response, refund, res.ShowId);
    }, ct);

    /// <summary>Records a refund the gateway has confirmed. Lock order: reservation → attempt.</summary>
    public Task<string?> MarkRefundedAsync(Guid attemptId, CancellationToken ct) => db.InTx(async (c, tx) =>
    {
        var now = time.GetUtcNow().UtcDateTime;
        var reservationId = await c.ExecuteScalarAsync<Guid>("SELECT reservation_id FROM payment_attempts WHERE id = @attemptId",
            new { attemptId }, tx);
        await c.ExecuteAsync("SELECT 1 FROM reservations WHERE id = @reservationId FOR UPDATE", new { reservationId }, tx);
        var attempt = await c.QuerySingleAsync<AttemptRow>($"SELECT {AttemptColumns} FROM payment_attempts WHERE id = @attemptId FOR UPDATE",
            new { attemptId }, tx);
        if (attempt.Status != "refund_pending") return null;

        await c.ExecuteAsync(
            """
            INSERT INTO payments (id, reservation_id, attempt_id, kind, amount_paise, gateway_ref, reason, created_at)
            VALUES (@id, @reservationId, @attemptId, 'refund', @amount, @gatewayRef, @reason, @now)
            ON CONFLICT (gateway_ref, kind) DO NOTHING
            """,
            new { id = Guid.CreateVersion7(), reservationId, attemptId, amount = attempt.AmountPaise, gatewayRef = attempt.GatewayKey,
                  reason = attempt.RefundReason, now }, tx);
        await SetAttemptAsync(c, tx, attemptId, "refunded", now, attempt.RefundReason);
        await c.ExecuteAsync("UPDATE reservations SET status = 'refunded', updated_at = @now WHERE id = @reservationId AND status = 'refund_pending'",
            new { reservationId, now }, tx);
        SeatResMetrics.Refunds.WithLabels(attempt.RefundReason ?? "cancel").Inc();
        return attempt.RefundReason;
    }, ct);

    public async Task<AttemptRow?> GetAttemptAsync(Guid attemptId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<AttemptRow>($"SELECT {AttemptColumns} FROM payment_attempts WHERE id = @attemptId",
            new { attemptId });
    }

    private static Task RecordChargeAsync(NpgsqlConnection c, NpgsqlTransaction tx, AttemptRow attempt, DateTime now) =>
        c.ExecuteAsync(
            """
            INSERT INTO payments (id, reservation_id, attempt_id, kind, amount_paise, gateway_ref, created_at)
            VALUES (@id, @reservationId, @attemptId, 'charge', @amount, @gatewayRef, @now)
            ON CONFLICT (gateway_ref, kind) DO NOTHING
            """,
            new { id = Guid.CreateVersion7(), reservationId = attempt.ReservationId, attemptId = attempt.Id,
                  amount = attempt.AmountPaise, gatewayRef = attempt.GatewayKey, now }, tx);

    internal static Task SetAttemptAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid attemptId, string status, DateTime now,
        string? refundReason = null) =>
        c.ExecuteAsync(
            "UPDATE payment_attempts SET status = @status, refund_reason = coalesce(@refundReason, refund_reason), updated_at = @now WHERE id = @attemptId",
            new { attemptId, status, now, refundReason }, tx);

    /// <summary>Frees only seats this reservation still owns — never a seat that now belongs to someone else.</summary>
    internal static async Task<int[]> ReleaseOwnedSeatsAsync(NpgsqlConnection c, NpgsqlTransaction tx, ReservationRow res) =>
        (await c.QueryAsync<int>(
            """
            UPDATE seats SET status = 'available', reservation_id = NULL, holder_user_id = NULL, hold_expires_at = NULL, pay_deadline = NULL
            WHERE show_id = @showId AND reservation_id = @id
            RETURNING seat_no
            """,
            new { showId = res.ShowId, id = res.Id }, tx)).ToArray();

    private static ReservationDto ConfirmedDto(ReservationRow res, DateTime now) =>
        ReservationStore.ToDto(res, now) with { Status = ReservationStatus.Confirmed, ExpiresAt = null, PayDeadline = null };

    private static ApiResult HoldLost(bool refunded = false) =>
        ApiResult.Fail(409, ErrorCodes.HoldLost,
            refunded
                ? "the hold expired and the seat was claimed by someone else before payment completed; the payment is refunded"
                : "the hold expired and the seat was claimed by someone else",
            refunded ? new Dictionary<string, object?> { ["refunded"] = true } : null);
}
