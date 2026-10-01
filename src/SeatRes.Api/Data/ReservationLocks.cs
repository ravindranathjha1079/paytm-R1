using Dapper;
using Npgsql;

namespace SeatRes.Api.Data;

public sealed record LockedReservation(ReservationRow Reservation, IReadOnlyList<SeatRow> Seats)
{
    /// <summary>True while every seat of the reservation is still owned by it (nobody reclaimed an expired one).</summary>
    public bool OwnsAllSeats =>
        Seats.Count == Reservation.SeatNos.Length && Seats.All(s => s.ReservationId == Reservation.Id);
}

/// <summary>Locks a reservation in the global order: owner's user lock → its seats by seat_no → the reservation row.</summary>
public static class ReservationLocks
{
    public static Task<ReservationRow?> HeadAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid id) =>
        c.QuerySingleOrDefaultAsync<ReservationRow>($"SELECT {Sql.ReservationColumns} FROM reservations WHERE id = @id", new { id }, tx);

    public static async Task<LockedReservation> LockAsync(NpgsqlConnection c, NpgsqlTransaction tx, ReservationRow head)
    {
        await UserLock.AcquireAsync(c, tx, head.UserId, head.ShowId);
        var seats = (await c.QueryAsync<SeatRow>(
            $"SELECT {Sql.SeatColumns} FROM seats WHERE show_id = @showId AND seat_no = ANY(@nos) ORDER BY seat_no FOR UPDATE",
            new { showId = head.ShowId, nos = head.SeatNos }, tx)).ToList();
        var reservation = await c.QuerySingleAsync<ReservationRow>(
            $"SELECT {Sql.ReservationColumns} FROM reservations WHERE id = @id FOR UPDATE", new { id = head.Id }, tx);
        return new LockedReservation(reservation, seats);
    }
}
