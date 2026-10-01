using Dapper;
using Npgsql;

namespace SeatRes.Api.Data;

/// <summary>
/// Step 1 of the global lock order. One row per (user, show): concurrent writes by the same user for
/// the same show queue here, which is what makes the per-user limit hold under parallel requests.
/// </summary>
public static class UserLock
{
    public static Task AcquireAsync(NpgsqlConnection c, NpgsqlTransaction tx, string userId, Guid showId) =>
        c.ExecuteAsync(
            """
            INSERT INTO user_show_locks (user_id, show_id) VALUES (@userId, @showId)
            ON CONFLICT (user_id, show_id) DO UPDATE SET user_id = EXCLUDED.user_id
            """,
            new { userId, showId }, tx);
}
