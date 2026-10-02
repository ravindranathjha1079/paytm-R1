using System.Net;
using SeatRes.Api.Payments;
using SeatRes.IntegrationTests.Infrastructure;

namespace SeatRes.IntegrationTests;

/// <summary>The memory-only decline must be exact: any key that was ever used is checked against the database.</summary>
[Collection("db")]
public class SeenKeysTests : IDisposable
{
    private readonly PostgresFixture _db;
    private DbApiFactory Api => _db.FastApi;

    public SeenKeysTests(PostgresFixture db)
    {
        _db = db;
        db.FastApi.Gateway.Reset();
    }

    public void Dispose() => Api.Gateway.Release(GatewayStatus.Declined);

    [Fact]
    public async Task Reusing_a_key_for_a_seat_someone_else_holds_is_still_an_idempotency_mismatch()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        var me = await client.TokenAsync(TestClients.NewUser());
        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(client, me, show, ["A1"], "k-reuse")).Status);
        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A2"])).Status);

        var (status, body) = await Reserve.SendAsync(client, me, show, ["A2"], "k-reuse");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("idempotency_mismatch", body.Error());
    }

    [Fact]
    public async Task A_winning_retry_still_replays_after_the_seat_was_cancelled_and_regranted_within_the_cache_window()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        var me = await client.TokenAsync(TestClients.NewUser());
        var (_, mine) = await Reserve.SendAsync(client, me, show, ["A1"], "k-win");
        var id = mine.GetProperty("reservation_id").GetGuid();
        await client.SendJsonAsync(HttpMethod.Post, $"/reservations/{id}/cancel", null, me);
        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"])).Status);

        var (status, replay) = await Reserve.SendAsync(client, me, show, ["A1"], "k-win");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(id, replay.GetProperty("reservation_id").GetGuid());
    }

    [Fact]
    public async Task Keys_used_before_a_restart_are_still_known_after_it()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        var me = await client.TokenAsync(TestClients.NewUser());
        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(client, me, show, ["A1"], "k-restart")).Status);

        // A second API process over the same database stands in for a restart.
        await using var restarted = new DbApiFactory(_db.ConnectionString, fastPath: true);
        var fresh = await restarted.ReadyClientAsync();
        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(fresh, await fresh.TokenAsync(TestClients.NewUser()), show, ["A2"])).Status);

        var (status, body) = await Reserve.SendAsync(fresh, me, show, ["A2"], "k-restart");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("idempotency_mismatch", body.Error());
    }
}
