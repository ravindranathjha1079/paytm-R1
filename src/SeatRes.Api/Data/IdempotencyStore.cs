using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using SeatRes.Api.Domain;

namespace SeatRes.Api.Data;

public static class IdemScope
{
    public const string Reserve = "reserve";
    public const string Confirm = "confirm";
}

public sealed record IdemRecord(byte[] RequestHash, int? ResponseStatus, string? ResponseBody, Guid? ReservationId);

/// <summary>
/// Exactly-once for a (user, scope, key): the key row is inserted inside the deciding transaction,
/// and the final response is stored with it, so a retry replays the original outcome.
/// </summary>
public static class IdempotencyStore
{
    private const string Select =
        "SELECT request_hash AS RequestHash, response_status AS ResponseStatus, response_body::text AS ResponseBody, " +
        "reservation_id AS ReservationId " +
        "FROM idempotency_keys WHERE user_id = @user AND scope = @scope AND key = @key";

    /// <summary>
    /// Inserts the key. If another transaction holds an uncommitted insert of the same key, Postgres makes this
    /// statement wait for it, so the caller always sees a committed outcome on conflict.
    /// </summary>
    public static async Task<bool> TryInsertAsync(NpgsqlConnection c, NpgsqlTransaction tx, string user, string scope,
        string key, byte[] hash, DateTime now) =>
        await c.ExecuteAsync(
            """
            INSERT INTO idempotency_keys (user_id, scope, key, request_hash, created_at)
            VALUES (@user, @scope, @key, @hash, @now) ON CONFLICT DO NOTHING
            """,
            new { user, scope, key, hash, now }, tx) == 1;

    public static Task<IdemRecord?> GetAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string user, string scope, string key) =>
        c.QuerySingleOrDefaultAsync<IdemRecord>(Select, new { user, scope, key }, tx);

    public static async Task<IdemRecord?> FindAsync(NpgsqlDataSource ds, string user, string scope, string key, CancellationToken ct)
    {
        Observability.SeatResMetrics.IdempotencyLookups.Inc();
        await using var c = await ds.OpenConnectionAsync(ct);
        return await GetAsync(c, null, user, scope, key);
    }

    public static Task StoreAsync(NpgsqlConnection c, NpgsqlTransaction tx, string user, string scope, string key,
        ApiResult result, Guid? reservationId) =>
        c.ExecuteAsync(
            """
            UPDATE idempotency_keys
            SET response_status = @status, response_body = @body::jsonb, reservation_id = coalesce(@reservationId, reservation_id)
            WHERE user_id = @user AND scope = @scope AND key = @key
            """,
            new { user, scope, key, status = result.StatusCode, body = result.Body?.ToJsonString(), reservationId }, tx);

    /// <summary>Turns a stored record into the response for a retry.</summary>
    public static ApiResult Replay(IdemRecord record, byte[] hash)
    {
        if (!CryptographicOperations.FixedTimeEquals(record.RequestHash, hash))
            return ApiResult.Fail(409, ErrorCodes.IdempotencyMismatch,
                "this idempotency key was already used with a different request");
        if (record.ResponseStatus is not { } status)
            return Processing(record.ReservationId);
        var body = JsonNode.Parse(record.ResponseBody!)!.AsObject();
        // A replay of a creation is not a creation: 201 becomes 200 so a storm still has exactly one 201.
        return new ApiResult(status == 201 ? 200 : status, body,
            body.ContainsKey("error") ? body["error"]!.GetValue<string>() : ErrorCodes.IdempotentReplay, Replayed: true);
    }

    public static ApiResult Processing(Guid? reservationId = null)
    {
        var body = new JsonObject
        {
            ["status"] = "payment_processing",
            ["message"] = "payment is still being processed; retry with the same key or poll GET /reservations/{id}",
        };
        if (reservationId is { } id) body["reservation_id"] = id.ToString();
        return new ApiResult(202, body, "payment_processing");
    }

    /// <summary>Links an in-progress key to its reservation so a replay can say what to poll.</summary>
    public static Task LinkAsync(NpgsqlConnection c, NpgsqlTransaction tx, string user, string scope, string key, Guid reservationId) =>
        c.ExecuteAsync("UPDATE idempotency_keys SET reservation_id = @reservationId WHERE user_id = @user AND scope = @scope AND key = @key",
            new { user, scope, key, reservationId }, tx);
}
