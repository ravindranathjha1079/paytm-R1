using System.Net;
using SeatRes.IntegrationTests.Infrastructure;

namespace SeatRes.IntegrationTests;

[Collection("db")]
public class HoldExpiryTests(PostgresFixture db)
{
    private DbApiFactory Api => db.Api;

    [Fact]
    public async Task An_expired_hold_reads_as_available_and_can_be_claimed_by_someone_else()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        var ownerToken = await client.TokenAsync(TestClients.NewUser());
        var (_, held) = await Reserve.SendAsync(client, ownerToken, show, ["A1"]);
        var reservationId = held.GetProperty("reservation_id").GetGuid();

        Api.Clock.Advance(TimeSpan.FromSeconds(299));
        Assert.Equal(HttpStatusCode.Conflict, (await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"])).Status);

        Api.Clock.Advance(TimeSpan.FromSeconds(1)); // exactly at expiry: takeable
        Assert.Equal(0, (await client.ShowAsync(show)).GetProperty("counts").GetProperty("held").GetInt32());
        var (status, _) = await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"]);
        Assert.Equal(HttpStatusCode.Created, status);

        var original = await client.SendJsonAsync(HttpMethod.Get, $"/reservations/{reservationId}", null, ownerToken);
        Assert.Equal("expired", (await original.JsonAsync()).GetProperty("status").GetString());
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Expired_holds_stop_counting_towards_the_per_user_limit()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 10), perUserLimit: 2);
        var token = await client.TokenAsync(TestClients.NewUser());
        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(client, token, show, ["A1", "A2"])).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Reserve.SendAsync(client, token, show, ["A3"])).Status);

        Api.Clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(client, token, show, ["A3", "A4"])).Status);
    }

    [Fact]
    public async Task Reading_a_reservation_is_owner_only()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        var ownerToken = await client.TokenAsync(TestClients.NewUser());
        var (_, held) = await Reserve.SendAsync(client, ownerToken, show, ["A1"]);
        var url = $"/reservations/{held.GetProperty("reservation_id").GetGuid()}";

        var mine = await client.SendJsonAsync(HttpMethod.Get, url, null, ownerToken);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal("held", (await mine.JsonAsync()).GetProperty("status").GetString());

        var theirs = await client.SendJsonAsync(HttpMethod.Get, url, null, await client.TokenAsync(TestClients.NewUser()));
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
    }
}
