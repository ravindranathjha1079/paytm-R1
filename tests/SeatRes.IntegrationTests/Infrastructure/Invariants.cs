using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace SeatRes.IntegrationTests.Infrastructure;

public static class Invariants
{
    /// <summary>Reconciliation + ownership + ledger checks that must hold after every scenario.</summary>
    public static async Task AssertAsync(DbApiFactory api, HttpClient client, Guid showId)
    {
        var show = await client.ShowAsync(showId);
        var counts = show.GetProperty("counts");
        var total = show.GetProperty("total_seats").GetInt32();
        Assert.Equal(total, counts.GetProperty("available").GetInt32() + counts.GetProperty("held").GetInt32()
                            + counts.GetProperty("confirmed").GetInt32());
        Assert.True(show.GetProperty("reconciled").GetBoolean());

        var ds = api.Services.GetRequiredService<NpgsqlDataSource>();
        await using var c = await ds.OpenConnectionAsync();
        // Every owned seat points at a reservation of the same user and show.
        var orphans = await c.ExecuteScalarAsync<long>(
            """
            SELECT count(*) FROM seats s LEFT JOIN reservations r ON r.id = s.reservation_id
            WHERE s.show_id = @showId AND s.reservation_id IS NOT NULL
              AND (r.id IS NULL OR r.user_id <> s.holder_user_id OR r.show_id <> s.show_id
                   OR NOT (s.seat_no = ANY (r.seat_nos)))
            """, new { showId });
        Assert.Equal(0, orphans);
        // Confirmed seats belong to confirmed reservations.
        var badConfirmed = await c.ExecuteScalarAsync<long>(
            """
            SELECT count(*) FROM seats s JOIN reservations r ON r.id = s.reservation_id
            WHERE s.show_id = @showId AND s.status = 'confirmed' AND r.status <> 'confirmed'
            """, new { showId });
        Assert.Equal(0, badConfirmed);

        var settled = await c.ExecuteScalarAsync<long>(
            """
            SELECT count(*) FROM payment_attempts a JOIN reservations r ON r.id = a.reservation_id
            WHERE r.show_id = @showId AND a.status IN ('pending', 'unknown', 'refund_pending')
            """, new { showId });
        if (settled == 0)
        {
            var ledger = show.GetProperty("ledger");
            Assert.Equal(ledger.GetProperty("expected_net_paise").GetInt64(), ledger.GetProperty("net_paise").GetInt64());
        }
    }

    public static void NoServerErrors(IEnumerable<HttpResponseMessage> responses) =>
        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
}

public static class Reserve
{
    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string token, Guid showId, string[] seats,
        string? key = null, bool? confirm = null, string? spoofUserId = null, IDictionary<string, string>? headers = null,
        CancellationToken ct = default) =>
        client.SendJsonAsync(HttpMethod.Post, $"/shows/{showId}/reserve",
            new { seats, idempotency_key = key ?? Guid.NewGuid().ToString("N"), confirm, user_id = spoofUserId },
            token, headers, ct);

    public static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpClient client, string token, Guid showId,
        string[] seats, string? key = null, bool? confirm = null)
    {
        var res = await PostAsync(client, token, showId, seats, key, confirm);
        return (res.StatusCode, await res.JsonAsync());
    }

    public static string[] SeatsOf(JsonElement reservation) =>
        reservation.GetProperty("seats").EnumerateArray().Select(s => s.GetString()!).ToArray();
}
