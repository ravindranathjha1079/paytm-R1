using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using SeatRes.Api.Domain;
using SeatRes.Api.Observability;

namespace SeatRes.Api.Data;

public sealed record ReserveCommand(ShowInfo Show, string UserId, string IdemKey, byte[] RequestHash, int[] SeatNos, bool Confirm,
    string? GatewayHint = null);

public sealed record TakenSeat(int SeatNo, DateTime? Until, string? Holder = null);

public sealed record ReserveTxResult(
    ApiResult Response, Guid? ReservationId, int[] ClaimedSeatNos, DateTime? ClaimedUntil,
    IReadOnlyList<TakenSeat> TakenSeats, PinnedPayment? Pinned, int ReclaimedHolds = 0);

/// <summary>
/// The atomic seat decision. One READ COMMITTED transaction:
///   idempotency key insert → user lock → live-seat count → seat rows FOR UPDATE (seat_no order)
///   → takeability check → conditional UPDATE → reservation insert.
/// The check and the claim happen while holding the seat row locks, so a concurrent claimer blocks,
/// then re-reads the committed row, sees it is not takeable, and declines.
/// </summary>
public sealed class ReserveTx(Db db, TimeProvider time, IOptions<SeatResOptions> options, ILogger<ReserveTx> log)
{
    public Task<ReserveTxResult> ExecuteAsync(ReserveCommand cmd, CancellationToken ct) => db.InTx(async (c, tx) =>
    {
        var now = time.GetUtcNow().UtcDateTime;

        if (!await IdempotencyStore.TryInsertAsync(c, tx, cmd.UserId, IdemScope.Reserve, cmd.IdemKey, cmd.RequestHash, now))
        {
            var existing = await IdempotencyStore.GetAsync(c, tx, cmd.UserId, IdemScope.Reserve, cmd.IdemKey);
            return Done(IdempotencyStore.Replay(existing!, cmd.RequestHash));
        }

        await c.ExecuteAsync("SAVEPOINT claim", transaction: tx);
        var result = await DecideAsync(c, tx, cmd, now);

        if (result.Response.StatusCode >= 400)
            await c.ExecuteAsync("ROLLBACK TO SAVEPOINT claim", transaction: tx);
        if (result.Pinned is null) // a pinned (hold-and-pay) request stores its response when payment finalizes
            await IdempotencyStore.StoreAsync(c, tx, cmd.UserId, IdemScope.Reserve, cmd.IdemKey, result.Response, result.ReservationId);
        else
            await IdempotencyStore.LinkAsync(c, tx, cmd.UserId, IdemScope.Reserve, cmd.IdemKey, result.ReservationId!.Value);
        return result;
    }, ct);

    private async Task<ReserveTxResult> DecideAsync(NpgsqlConnection c, NpgsqlTransaction tx, ReserveCommand cmd, DateTime now)
    {
        var show = cmd.Show;
        var opt = options.Value;

        await UserLock.AcquireAsync(c, tx, cmd.UserId, show.Id);

        var live = await c.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM seats WHERE show_id = @showId AND holder_user_id = @userId AND NOT {SeatRules.TakeableSql}",
            new { showId = show.Id, userId = cmd.UserId, now }, tx);
        if (live + cmd.SeatNos.Length > show.PerUserLimit)
            return Done(ApiResult.Fail(409, ErrorCodes.PerUserLimit,
                $"a user may hold at most {show.PerUserLimit} seats for this show",
                new Dictionary<string, object?> { ["limit"] = show.PerUserLimit, ["already_held"] = live }));

        var rows = (await c.QueryAsync<SeatRow>(
            $"SELECT {Sql.SeatColumns} FROM seats WHERE show_id = @showId AND seat_no = ANY(@nos) ORDER BY seat_no FOR UPDATE",
            new { showId = show.Id, nos = cmd.SeatNos }, tx)).ToList();

        var taken = rows.Where(r => !SeatRules.IsTakeable(r.Status, r.HoldExpiresAt, r.PayDeadline, now)).ToList();
        if (taken.Count > 0)
            return new ReserveTxResult(
                ApiResult.Fail(409, ErrorCodes.SeatTaken, "one or more requested seats are already taken",
                    new Dictionary<string, object?>
                    {
                        ["taken"] = taken.Select(t => t.Label).ToArray(),
                        ["held_by_you"] = taken.All(t => t.HolderUserId == cmd.UserId),
                    }),
                null, [], null,
                taken.Select(t => new TakenSeat(t.SeatNo, t.Status switch
                {
                    SeatStatus.Held => t.HoldExpiresAt,
                    SeatStatus.PaymentPending => t.PayDeadline,
                    _ => null,
                }, t.HolderUserId)).ToList(),
                null);

        var previousOwners = rows.Where(r => r.ReservationId is not null).Select(r => r.ReservationId!.Value)
            .Distinct().Order().ToArray();
        var reservationId = Guid.CreateVersion7();
        // Hold-and-pay never has a takeable hold: the seats go straight from claim to a payment pin.
        DateTime? holdUntil = cmd.Confirm ? null : now + opt.Hold;
        var amount = checked(show.PricePaise * cmd.SeatNos.Length);
        var labels = cmd.SeatNos.Select(show.LabelOf).ToArray();

        var claimed = await c.ExecuteAsync(
            $"""
            UPDATE seats SET status = 'held', reservation_id = @reservationId, holder_user_id = @userId,
                             hold_expires_at = @holdUntil, pay_deadline = NULL
            WHERE show_id = @showId AND seat_no = ANY(@nos) AND {SeatRules.TakeableSql}
            """,
            new { reservationId, userId = cmd.UserId, holdUntil, showId = show.Id, nos = cmd.SeatNos, now }, tx);
        if (claimed != cmd.SeatNos.Length)
        {
            // Impossible while the rows are locked; decline rather than oversell if it ever happens.
            log.LogError("seat_claim_mismatch {show_id} {expected} {claimed}", show.Id, cmd.SeatNos.Length, claimed);
            return Done(ApiResult.Fail(409, ErrorCodes.SeatTaken, "one or more requested seats are already taken"));
        }

        await c.ExecuteAsync(
            """
            INSERT INTO reservations (id, show_id, user_id, seat_nos, labels, amount_paise, status, hold_expires_at, created_at, updated_at)
            VALUES (@reservationId, @showId, @userId, @nos, @labels, @amount, 'held', @holdUntil, @now, @now)
            """,
            new { reservationId, showId = show.Id, userId = cmd.UserId, nos = cmd.SeatNos, labels, amount, holdUntil, now }, tx);

        if (previousOwners.Length > 0)
        {
            await c.ExecuteAsync(
                "UPDATE reservations SET status = 'expired', updated_at = @now WHERE id = ANY(@ids) AND status IN ('held', 'payment_pending')",
                new { ids = previousOwners, now }, tx);
        }

        var dto = new ReservationDto(reservationId, show.Id, cmd.UserId, labels, amount, ReservationStatus.Held,
            holdUntil, null, now);
        if (!cmd.Confirm)
            return new ReserveTxResult(ApiResult.Ok(dto, 201), reservationId, cmd.SeatNos, holdUntil, [], null, previousOwners.Length);

        var reservation = new ReservationRow
        {
            Id = reservationId, ShowId = show.Id, UserId = cmd.UserId, SeatNos = cmd.SeatNos, Labels = labels,
            AmountPaise = amount, Status = ReservationStatus.Held,
        };
        var deadline = SeatRules.PayDeadline("new", null, null, now, opt.PayGrace);
        var pinned = await PaymentTx.CreatePinAsync(c, tx, reservation, cmd.UserId, IdemScope.Reserve, cmd.IdemKey, deadline, now, cmd.GatewayHint);
        return new ReserveTxResult(ApiResult.Ok(dto with { Status = ReservationStatus.PaymentPending, PayDeadline = deadline }, 202),
            reservationId, cmd.SeatNos, deadline, [], pinned, previousOwners.Length);
    }

    private static ReserveTxResult Done(ApiResult response) => new(response, null, [], null, [], null);
}
