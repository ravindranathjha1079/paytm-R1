using Dapper;
using SeatRes.Api.Domain;

namespace SeatRes.Api.Data;

public sealed class ReservationStore(Db db)
{
    public async Task<ReservationRow?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<ReservationRow>(
            $"SELECT {Sql.ReservationColumns} FROM reservations WHERE id = @id", new { id });
    }

    public static ReservationDto ToDto(ReservationRow r, DateTime now) =>
        new(r.Id, r.ShowId, r.UserId, r.Labels, r.AmountPaise, r.Status, r.HoldExpiresAt, r.PayDeadline, now);
}
