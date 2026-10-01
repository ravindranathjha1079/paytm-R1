using System.Net;
using SeatRes.Api.Payments;
using SeatRes.IntegrationTests.Infrastructure;

namespace SeatRes.IntegrationTests;

[Collection("db")]
public class CancelAndHoldPayTests : IDisposable
{
    private readonly PostgresFixture _db;
    private DbApiFactory Api => _db.Api;

    public CancelAndHoldPayTests(PostgresFixture db)
    {
        _db = db;
        db.Api.Gateway.Reset();
    }

    public void Dispose() => Api.Gateway.Release(GatewayStatus.Declined);

    private static Task<HttpResponseMessage> Cancel(HttpClient client, string token, Guid reservationId) =>
        client.SendJsonAsync(HttpMethod.Post, $"/reservations/{reservationId}/cancel", null, token);

    private static Task<HttpResponseMessage> Confirm(HttpClient client, string token, Guid reservationId) =>
        client.SendJsonAsync(HttpMethod.Post, $"/reservations/{reservationId}/confirm", null, token,
            new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString("N") });

    [Fact]
    public async Task Hold_and_pay_in_one_call_returns_201_confirmed()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        var (status, body) = await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1", "A2"], confirm: true);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("confirmed", body.GetProperty("status").GetString());
        Assert.Equal(50_000, body.GetProperty("amount_paise").GetInt64());
        var state = await client.ShowAsync(show);
        Assert.Equal(2, state.GetProperty("counts").GetProperty("confirmed").GetInt32());
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Hold_and_pay_retried_with_the_same_key_replays_without_a_second_charge()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        var token = await client.TokenAsync(TestClients.NewUser());
        var first = await Reserve.SendAsync(client, token, show, ["A1"], key: "hp-1", confirm: true);
        var again = await Reserve.SendAsync(client, token, show, ["A1"], key: "hp-1", confirm: true);
        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal("confirmed", again.Body.GetProperty("status").GetString());
        Assert.Equal(1, Api.Gateway.ChargeCalls);
    }

    [Fact]
    public async Task Hold_and_pay_in_flight_replays_202()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        var token = await client.TokenAsync(TestClients.NewUser());
        Api.Gateway.Plan(Script.Block);
        var paying = Reserve.PostAsync(client, token, show, ["A1"], key: "hp-2", confirm: true);
        await Api.Gateway.Entered;

        var replay = await Reserve.SendAsync(client, token, show, ["A1"], key: "hp-2", confirm: true);
        Assert.Equal(HttpStatusCode.Accepted, replay.Status);

        Api.Gateway.Release(GatewayStatus.Succeeded);
        Assert.Equal(HttpStatusCode.Created, (await paying).StatusCode);
    }

    [Fact]
    public async Task Declined_hold_and_pay_releases_the_seats()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        Api.Gateway.Plan(Script.Decline);
        var (status, body) = await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"], confirm: true);
        Assert.Equal(HttpStatusCode.PaymentRequired, status);
        Assert.Equal("payment_declined", body.Error());
        Assert.Equal(3, (await client.ShowAsync(show)).GetProperty("counts").GetProperty("available").GetInt32());
        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"])).Status);
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Cancelling_a_hold_makes_the_seat_rebookable()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        var token = await client.TokenAsync(TestClients.NewUser());
        var (_, hold) = await Reserve.SendAsync(client, token, show, ["A1"]);

        var res = await Cancel(client, token, hold.GetProperty("reservation_id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("cancelled", (await res.JsonAsync()).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"])).Status);
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Only_the_owner_can_cancel()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        var (_, hold) = await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"]);

        var res = await Cancel(client, await client.TokenAsync(TestClients.NewUser()), hold.GetProperty("reservation_id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(1, (await client.ShowAsync(show)).GetProperty("counts").GetProperty("held").GetInt32());
    }

    [Fact]
    public async Task Cancelling_a_confirmed_booking_refunds_it()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        var token = await client.TokenAsync(TestClients.NewUser());
        var (_, booked) = await Reserve.SendAsync(client, token, show, ["A1"], confirm: true);

        var res = await Cancel(client, token, booked.GetProperty("reservation_id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("refunded", (await res.JsonAsync()).GetProperty("status").GetString());
        var state = await client.ShowAsync(show);
        Assert.Equal(3, state.GetProperty("counts").GetProperty("available").GetInt32());
        Assert.Equal(0, state.GetProperty("ledger").GetProperty("net_paise").GetInt64());
        Assert.Equal(25_000, state.GetProperty("ledger").GetProperty("refunded_paise").GetInt64());
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Cancelling_an_expired_reclaimed_hold_never_resurrects_the_new_owners_seat()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        var token = await client.TokenAsync(TestClients.NewUser());
        var (_, hold) = await Reserve.SendAsync(client, token, show, ["A1"]);
        Api.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"])).Status);

        var res = await Cancel(client, token, hold.GetProperty("reservation_id").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("reservation_closed", (await res.JsonAsync()).Error());
        Assert.Equal(1, (await client.ShowAsync(show)).GetProperty("counts").GetProperty("held").GetInt32());
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Cancel_is_idempotent()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        var token = await client.TokenAsync(TestClients.NewUser());
        var (_, hold) = await Reserve.SendAsync(client, token, show, ["A1"]);
        var id = hold.GetProperty("reservation_id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Cancel(client, token, id)).StatusCode);
        var again = await Cancel(client, token, id);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("cancelled", (await again.JsonAsync()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Cancel_while_a_payment_is_processing_is_refused()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        var token = await client.TokenAsync(TestClients.NewUser());
        var (_, hold) = await Reserve.SendAsync(client, token, show, ["A1"]);
        var id = hold.GetProperty("reservation_id").GetGuid();
        Api.Gateway.Plan(Script.Block);
        var paying = Confirm(client, token, id);
        await Api.Gateway.Entered;

        var res = await Cancel(client, token, id);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("payment_in_progress", (await res.JsonAsync()).Error());

        Api.Gateway.Release(GatewayStatus.Succeeded);
        Assert.Equal(HttpStatusCode.OK, (await paying).StatusCode);
    }
}
