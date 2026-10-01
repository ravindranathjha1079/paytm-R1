using System.Net;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SeatRes.Api.Background;
using SeatRes.Api.Payments;
using SeatRes.IntegrationTests.Infrastructure;

namespace SeatRes.IntegrationTests;

[Collection("db")]
public class BackgroundTests : IDisposable
{
    private readonly PostgresFixture _db;
    private DbApiFactory Api => _db.Api;

    public BackgroundTests(PostgresFixture db)
    {
        _db = db;
        db.Api.Gateway.Reset();
    }

    public void Dispose() => Api.Gateway.Release(GatewayStatus.Declined);

    private PaymentRecovery Recovery => Api.Services.GetRequiredService<PaymentRecovery>();
    private Reconciler Reconciler => Api.Services.GetRequiredService<Reconciler>();

    private static Task<HttpResponseMessage> Confirm(HttpClient client, string token, Guid id, string key) =>
        client.SendJsonAsync(HttpMethod.Post, $"/reservations/{id}/confirm", null, token,
            new Dictionary<string, string> { ["Idempotency-Key"] = key });

    private static async Task<string> StatusOf(HttpClient client, string token, Guid id) =>
        (await (await client.SendJsonAsync(HttpMethod.Get, $"/reservations/{id}", null, token)).JsonAsync()).GetProperty("status").GetString()!;

    private async Task<(HttpClient, Guid Show, string Token, Guid Id)> HoldAsync()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        var token = await client.TokenAsync(TestClients.NewUser());
        var (_, hold) = await Reserve.SendAsync(client, token, show, ["A1"]);
        return (client, show, token, hold.GetProperty("reservation_id").GetGuid());
    }

    [Fact]
    public async Task Recovery_confirms_a_payment_whose_response_was_lost()
    {
        var (client, show, token, id) = await HoldAsync();
        Api.Gateway.Plan(Script.UnknownButCharged);
        Assert.Equal(HttpStatusCode.Accepted, (await Confirm(client, token, id, "lost-1")).StatusCode);

        Api.Clock.Advance(TimeSpan.FromSeconds(31));
        await Recovery.RunOnceAsync(CancellationToken.None);

        Assert.Equal("confirmed", await StatusOf(client, token, id));
        var replay = await Confirm(client, token, id, "lost-1");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("confirmed", (await replay.JsonAsync()).GetProperty("status").GetString());
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Recovery_gives_up_on_a_charge_the_gateway_never_saw_only_after_two_minutes()
    {
        var (client, show, token, id) = await HoldAsync();
        Api.Gateway.Plan(Script.UnknownNotCharged);
        Assert.Equal(HttpStatusCode.Accepted, (await Confirm(client, token, id, "lost-2")).StatusCode);

        Api.Clock.Advance(TimeSpan.FromSeconds(31));
        await Recovery.RunOnceAsync(CancellationToken.None);
        Assert.Equal("payment_pending", await StatusOf(client, token, id));

        Api.Clock.Advance(TimeSpan.FromMinutes(2));
        await Recovery.RunOnceAsync(CancellationToken.None);
        var replay = await Confirm(client, token, id, "lost-2");
        Assert.Equal(HttpStatusCode.PaymentRequired, replay.StatusCode);
        // Still pinned (hold 5:00 + 1:00 grace) for the owner to retry; free for others once that lapses.
        Assert.Equal(HttpStatusCode.Conflict, (await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"])).Status);
        Api.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"])).Status);
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Recovery_retries_a_refund_the_gateway_rejected()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        var token = await client.TokenAsync(TestClients.NewUser());
        var (_, booked) = await Reserve.SendAsync(client, token, show, ["A1"], confirm: true);
        var id = booked.GetProperty("reservation_id").GetGuid();

        Api.Gateway.FailRefunds = true;
        var cancel = await client.SendJsonAsync(HttpMethod.Post, $"/reservations/{id}/cancel", null, token);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        Assert.Equal("refund_pending", await StatusOf(client, token, id));

        Api.Gateway.FailRefunds = false;
        await Recovery.RunOnceAsync(CancellationToken.None);

        Assert.Equal("refunded", await StatusOf(client, token, id));
        Assert.Equal(0, (await client.ShowAsync(show)).GetProperty("ledger").GetProperty("net_paise").GetInt64());
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Reconciler_passes_on_healthy_data_and_flags_a_ledger_that_does_not_add_up()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        var (_, booked) = await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"], confirm: true);
        var id = booked.GetProperty("reservation_id").GetGuid();

        var healthy = await Reconciler.RunOnceAsync(CancellationToken.None);
        Assert.True(healthy.SeatsOk, string.Join("; ", healthy.Problems));
        Assert.True(healthy.LedgerOk, string.Join("; ", healthy.Problems));

        var ds = Api.Services.GetRequiredService<NpgsqlDataSource>();
        await using var c = await ds.OpenConnectionAsync();
        var bogus = Guid.NewGuid();
        await c.ExecuteAsync(
            "INSERT INTO payments (id, reservation_id, kind, amount_paise, gateway_ref, created_at) VALUES (@bogus, @id, 'charge', 999, @bogusRef, now())",
            new { bogus, id, bogusRef = "tamper-" + bogus });
        try
        {
            var report = await Reconciler.RunOnceAsync(CancellationToken.None);
            Assert.False(report.LedgerOk);
            Assert.Contains("0", await client.GetStringAsync("/metrics").ContinueWith(t =>
                t.Result.Split('\n').Single(l => l.StartsWith("seatres_reconciliation_ok{check=\"ledger\"}"))));
        }
        finally
        {
            await c.ExecuteAsync("DELETE FROM payments WHERE id = @bogus", new { bogus });
        }
        Assert.True((await Reconciler.RunOnceAsync(CancellationToken.None)).LedgerOk);
    }

    [Fact]
    public async Task Seat_gauge_matches_the_api_and_confirmations_are_counted()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 4));
        var token = await client.TokenAsync(TestClients.NewUser());
        var (_, hold) = await Reserve.SendAsync(client, token, show, ["A1", "A2"]);
        await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A3"], confirm: true);
        await Api.Services.GetRequiredService<SeatGauge>().RefreshAsync(CancellationToken.None);

        var metrics = (await client.GetStringAsync("/metrics")).Split('\n');
        double Value(string prefix) => double.Parse(metrics.Single(l => l.StartsWith(prefix)).Split(' ')[^1],
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(2, Value($"seatres_seats{{show=\"{show}\",state=\"held\"}}"));
        Assert.Equal(1, Value($"seatres_seats{{show=\"{show}\",state=\"confirmed\"}}"));
        Assert.Equal(1, Value($"seatres_seats{{show=\"{show}\",state=\"available\"}}"));
        Assert.Equal(1, Value($"seatres_reservations_confirmed_total{{show=\"{show}\"}}"));
        Assert.Equal(1, Value($"seatres_holds_created_total{{show=\"{show}\"}}"));
    }
}
