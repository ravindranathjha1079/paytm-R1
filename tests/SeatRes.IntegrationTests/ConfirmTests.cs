using System.Net;
using System.Text.Json;
using SeatRes.Api.Payments;
using SeatRes.IntegrationTests.Infrastructure;

namespace SeatRes.IntegrationTests;

[Collection("db")]
public class ConfirmTests : IDisposable
{
    private readonly PostgresFixture _db;
    private DbApiFactory Api => _db.Api;

    public ConfirmTests(PostgresFixture db)
    {
        _db = db;
        db.Api.Gateway.Reset();
    }

    public void Dispose() => Api.Gateway.Release(GatewayStatus.Declined); // never leave a blocked charge behind

    private static Task<HttpResponseMessage> Confirm(HttpClient client, string token, Guid reservationId, string? key = null,
        CancellationToken ct = default) =>
        client.SendJsonAsync(HttpMethod.Post, $"/reservations/{reservationId}/confirm", null, token,
            new Dictionary<string, string> { ["Idempotency-Key"] = key ?? Guid.NewGuid().ToString("N") }, ct);

    private async Task<(HttpClient Client, Guid Show, string Token, Guid ReservationId, JsonElement Hold)> HoldAsync(
        int seats = 3, string[]? want = null, int? limit = null)
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", seats), perUserLimit: limit);
        var token = await client.TokenAsync(TestClients.NewUser());
        var (status, hold) = await Reserve.SendAsync(client, token, show, want ?? ["A1"]);
        Assert.Equal(HttpStatusCode.Created, status);
        return (client, show, token, hold.GetProperty("reservation_id").GetGuid(), hold);
    }

    private async Task<string> StatusOf(HttpClient client, string token, Guid reservationId) =>
        (await (await client.SendJsonAsync(HttpMethod.Get, $"/reservations/{reservationId}", null, token)).JsonAsync())
        .GetProperty("status").GetString()!;

    [Fact]
    public async Task Hold_then_confirm_charges_once_and_confirms()
    {
        var (client, show, token, rid, _) = await HoldAsync();
        var res = await Confirm(client, token, rid);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("confirmed", (await res.JsonAsync()).GetProperty("status").GetString());

        var state = await client.ShowAsync(show);
        Assert.Equal(1, state.GetProperty("counts").GetProperty("confirmed").GetInt32());
        Assert.Equal(25_000, state.GetProperty("ledger").GetProperty("charged_paise").GetInt64());
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Retrying_confirm_with_the_same_key_never_charges_twice()
    {
        var (client, show, token, rid, _) = await HoldAsync();
        var first = await Confirm(client, token, rid, "pay-1");
        var again = await Confirm(client, token, rid, "pay-1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("true", again.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(1, Api.Gateway.ChargeCalls);
        Assert.Equal(25_000, (await client.ShowAsync(show)).GetProperty("ledger").GetProperty("charged_paise").GetInt64());
    }

    [Fact]
    public async Task Only_the_owner_can_confirm()
    {
        var (client, _, _, rid, _) = await HoldAsync();
        var res = await Confirm(client, await client.TokenAsync(TestClients.NewUser()), rid);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(0, Api.Gateway.ChargeCalls);
    }

    [Fact]
    public async Task A_declined_payment_keeps_the_seat_pinned_and_can_be_retried()
    {
        var (client, show, token, rid, hold) = await HoldAsync();
        Api.Gateway.Plan(Script.Decline);

        var declined = await Confirm(client, token, rid);
        Assert.Equal(HttpStatusCode.PaymentRequired, declined.StatusCode);
        var body = await declined.JsonAsync();
        Assert.Equal("payment_declined", body.Error());
        Assert.Equal(hold.GetProperty("expires_at").GetDateTime().AddSeconds(60), body.GetProperty("retry_until").GetDateTime());

        Api.Clock.Advance(TimeSpan.FromSeconds(320)); // past the hold, inside the +60 s payment grace
        Assert.Equal(HttpStatusCode.Conflict, (await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"])).Status);

        var retry = await Confirm(client, token, rid);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(1, Api.Gateway.Succeeded);
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task A_payment_in_progress_pins_the_seat_past_the_hold()
    {
        var (client, show, token, rid, _) = await HoldAsync();
        Api.Gateway.Plan(Script.Block);
        Api.Clock.Advance(TimeSpan.FromSeconds(270)); // 4:30 into the hold

        var paying = Confirm(client, token, rid);
        await Api.Gateway.Entered;
        Api.Clock.Advance(TimeSpan.FromSeconds(60)); // 5:30: hold over, payment pin still running
        var rival = await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"]);
        Assert.Equal(HttpStatusCode.Conflict, rival.Status);

        Api.Gateway.Release(GatewayStatus.Succeeded);
        Assert.Equal(HttpStatusCode.OK, (await paying).StatusCode);
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Late_confirm_succeeds_while_nobody_else_has_claimed_the_seat()
    {
        var (client, show, token, rid, _) = await HoldAsync();
        Api.Clock.Advance(TimeSpan.FromMinutes(7));
        var res = await Confirm(client, token, rid);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Confirm_after_someone_else_claimed_the_seat_is_hold_lost_and_charges_nothing()
    {
        var (client, show, token, rid, _) = await HoldAsync();
        Api.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"])).Status);

        var res = await Confirm(client, token, rid);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("hold_lost", (await res.JsonAsync()).Error());
        Assert.Equal(0, Api.Gateway.ChargeCalls);
    }

    [Fact]
    public async Task Payment_that_succeeds_after_the_seat_was_lost_is_refunded()
    {
        var (client, show, token, rid, _) = await HoldAsync();
        Api.Gateway.Plan(Script.Block);
        Api.Clock.Advance(TimeSpan.FromSeconds(299)); // 4:59: pin runs to 6:00

        var paying = Confirm(client, token, rid);
        await Api.Gateway.Entered;
        Api.Clock.Advance(TimeSpan.FromSeconds(62)); // 6:01: pin expired while the gateway was still working
        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"])).Status);

        Api.Gateway.Release(GatewayStatus.Succeeded);
        var res = await paying;
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.JsonAsync();
        Assert.Equal("hold_lost", body.Error());
        Assert.True(body.GetProperty("refunded").GetBoolean());
        Assert.Equal("refunded", await StatusOf(client, token, rid));
        var ledger = (await client.ShowAsync(show)).GetProperty("ledger");
        Assert.Equal(25_000, ledger.GetProperty("charged_paise").GetInt64());
        Assert.Equal(25_000, ledger.GetProperty("refunded_paise").GetInt64());
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Concurrent_confirms_with_different_keys_do_not_start_a_second_payment()
    {
        var (client, _, token, rid, _) = await HoldAsync();
        Api.Gateway.Plan(Script.Block);
        var paying = Confirm(client, token, rid, "first");
        await Api.Gateway.Entered;

        var other = await Confirm(client, token, rid, "second");
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal("payment_in_progress", (await other.JsonAsync()).Error());

        var sameKey = await Confirm(client, token, rid, "first");
        Assert.Equal(HttpStatusCode.Accepted, sameKey.StatusCode);

        Api.Gateway.Release(GatewayStatus.Succeeded);
        Assert.Equal(HttpStatusCode.OK, (await paying).StatusCode);
        Assert.Equal(1, Api.Gateway.ChargeCalls);
    }

    [Fact]
    public async Task Late_confirm_rechecks_the_per_user_limit()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 10), perUserLimit: 2);
        var token = await client.TokenAsync(TestClients.NewUser());
        var (_, first) = await Reserve.SendAsync(client, token, show, ["A1", "A2"]);
        Api.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(client, token, show, ["A3", "A4"])).Status);

        var res = await Confirm(client, token, first.GetProperty("reservation_id").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("per_user_limit", (await res.JsonAsync()).Error());
    }

    [Fact]
    public async Task A_client_that_disconnects_mid_payment_still_gets_its_payment_finalized()
    {
        var (client, show, token, rid, _) = await HoldAsync();
        Api.Gateway.Plan(Script.Block);
        using var cts = new CancellationTokenSource();
        var paying = Confirm(client, token, rid, ct: cts.Token);
        await Api.Gateway.Entered;

        cts.Cancel(); // the server sees RequestAborted while the gateway is still working
        await Task.Delay(100);
        Api.Gateway.Release(GatewayStatus.Succeeded);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => paying);

        for (var i = 0; i < 50 && await StatusOf(client, token, rid) != "confirmed"; i++) await Task.Delay(50);
        Assert.Equal("confirmed", await StatusOf(client, token, rid));
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Unknown_gateway_outcome_answers_202_and_keeps_the_seat_pinned()
    {
        var (client, show, token, rid, _) = await HoldAsync();
        Api.Gateway.Plan(Script.UnknownNotCharged);
        var res = await Confirm(client, token, rid);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal("payment_pending", await StatusOf(client, token, rid));
        Assert.Equal(1, (await client.ShowAsync(show)).GetProperty("counts").GetProperty("held").GetInt32());
    }
}
