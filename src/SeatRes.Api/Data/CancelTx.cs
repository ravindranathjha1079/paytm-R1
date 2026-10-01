using Dapper;
using SeatRes.Api.Domain;

namespace SeatRes.Api.Data;

public sealed record CancelResult(ApiResult Response, Guid? RefundAttemptId, Guid? ShowId, int[] ReleasedSeatNos);

/// <summary>
/// Owner-only release. Seats are freed with <c>WHERE reservation_id = @id</c>, so a cancel can never
/// free a seat that has since been claimed by someone else.
/// </summary>
public sealed class CancelTx(Db db, TimeProvider time)
{
    public Task<CancelResult> ExecuteAsync(Guid reservationId, string caller, CancellationToken ct) => db.InTx(async (c, tx) =>
    {
        var now = time.GetUtcNow().UtcDateTime;
        var head = await ReservationLocks.HeadAsync(c, tx, reservationId);
        if (head is null || head.UserId != caller)
            return new CancelResult(ApiResult.Fail(404, ErrorCodes.NotFound, "reservation not found"), null, null, []);

        var locked = await ReservationLocks.LockAsync(c, tx, head);
        var res = locked.Reservation;

        var inFlight = await c.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM payment_attempts WHERE reservation_id = @id AND status IN ('pending', 'unknown'))",
            new { id = res.Id }, tx);
        if (inFlight)
            return Done(ApiResult.Fail(409, ErrorCodes.PaymentInProgress, "a payment is being processed; retry the cancel shortly"));

        switch (res.Status)
        {
            case ReservationStatus.Held or ReservationStatus.PaymentPending:
            {
                var released = await PaymentTx.ReleaseOwnedSeatsAsync(c, tx, res);
                await SetStatusAsync(ReservationStatus.Cancelled);
                return new CancelResult(Ok(ReservationStatus.Cancelled), null, res.ShowId, released);
            }
            case ReservationStatus.Confirmed:
            {
                var released = await PaymentTx.ReleaseOwnedSeatsAsync(c, tx, res);
                await SetStatusAsync(ReservationStatus.RefundPending);
                var attemptId = await c.ExecuteScalarAsync<Guid?>(
                    """
                    UPDATE payment_attempts SET status = 'refund_pending', refund_reason = 'cancel', updated_at = @now
                    WHERE reservation_id = @id AND status = 'succeeded' RETURNING id
                    """, new { id = res.Id, now }, tx);
                return new CancelResult(Ok(ReservationStatus.Refunded), attemptId, res.ShowId, released);
            }
            case ReservationStatus.Cancelled or ReservationStatus.Refunded:
                return Done(Ok(res.Status));
            case ReservationStatus.RefundPending:
                return Done(RefundProcessing());
            default:
                return Done(ApiResult.Fail(409, ErrorCodes.ReservationClosed,
                    "this hold expired and its seat was claimed by someone else; there is nothing to cancel"));
        }

        Task SetStatusAsync(string status) => c.ExecuteAsync(
            "UPDATE reservations SET status = @status, updated_at = @now WHERE id = @id", new { status, now, id = res.Id }, tx);

        ApiResult Ok(string status) => ApiResult.Ok(ReservationStore.ToDto(res, now) with
        {
            Status = status, ExpiresAt = null, PayDeadline = null,
        });

        CancelResult Done(ApiResult r) => new(r, null, res.ShowId, []);
    }, ct);

    public static ApiResult RefundProcessing() =>
        new(202, new System.Text.Json.Nodes.JsonObject
        {
            ["status"] = ReservationStatus.RefundPending,
            ["message"] = "the seats are released and the refund is being processed",
        }, "refund_processing");
}
