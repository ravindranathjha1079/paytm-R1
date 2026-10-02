using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SeatRes.Api.Background;
using SeatRes.Api.Payments;
using SeatRes.IntegrationTests.Infrastructure;

namespace SeatRes.IntegrationTests;

[Collection("db")]
public class PolishTests : IDisposable
{
    private readonly PostgresFixture _db;

    public PolishTests(PostgresFixture db)
    {
        _db = db;
        db.Api.Gateway.Reset();
        db.FastApi.Gateway.Reset();
    }

    public void Dispose()
    {
        _db.Api.Gateway.Release(GatewayStatus.Declined);
        _db.FastApi.Gateway.Release(GatewayStatus.Declined);
    }

    private static Task<HttpResponseMessage> Confirm(HttpClient client, string token, Guid id) =>
        client.SendJsonAsync(HttpMethod.Post, $"/reservations/{id}/confirm", null, token,
            new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString("N") });

    [Fact]
    public async Task A_decline_that_arrives_after_the_seat_was_lost_says_hold_lost_not_retry()
    {
        var api = _db.Api;
        var client = await api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        var token = await client.TokenAsync(TestClients.NewUser());
        var (_, hold) = await Reserve.SendAsync(client, token, show, ["A1"]);
        api.Gateway.Plan(Script.Block);
        api.Clock.Advance(TimeSpan.FromSeconds(299));
        var paying = Confirm(client, token, hold.GetProperty("reservation_id").GetGuid());
        await api.Gateway.Entered;
        api.Clock.Advance(TimeSpan.FromSeconds(62));
        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"])).Status);

        api.Gateway.Release(GatewayStatus.Declined);
        var res = await paying;
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("hold_lost", (await res.JsonAsync()).Error());
    }

    [Fact]
    public async Task Seats_released_by_a_declined_hold_and_pay_are_rebookable_at_once_with_the_fast_path_on()
    {
        var api = _db.FastApi;
        var client = await api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        api.Gateway.Plan(Script.Decline);
        var (declined, _) = await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"], confirm: true);
        Assert.Equal(HttpStatusCode.PaymentRequired, declined);

        var (status, _) = await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"]);
        Assert.Equal(HttpStatusCode.Created, status);
    }

    [Fact]
    public async Task A_202_tells_the_client_which_reservation_to_poll()
    {
        var api = _db.Api;
        var client = await api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        api.Gateway.Plan(Script.UnknownNotCharged);
        var (status, body) = await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"], confirm: true);
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.NotEqual(Guid.Empty, body.GetProperty("reservation_id").GetGuid());
    }

    [Fact]
    public async Task Unhandled_errors_are_counted_as_5xx_in_http_metrics()
    {
        var client = await _db.Api.ReadyClientAsync();
        double FiveXx(string m) => m.Split('\n')
            .Where(l => l.StartsWith("http_requests_received_total{") && l.Contains("code=\"5"))
            .Sum(l => double.Parse(l.Split(' ')[^1], CultureInfo.InvariantCulture));
        var before = FiveXx(await client.GetStringAsync("/metrics"));

        var res = await client.GetAsync("/__test/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Equal("internal", (await res.JsonAsync()).Error());
        Assert.Equal(before + 1, FiveXx(await client.GetStringAsync("/metrics")));
    }

    [Fact]
    public async Task Show_state_can_be_polled_without_the_seat_list()
    {
        var client = await _db.Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        var body = await (await client.GetAsync($"/shows/{show}?seats=false")).JsonAsync();
        Assert.False(body.TryGetProperty("seats", out _));
        Assert.Equal(5, body.GetProperty("counts").GetProperty("available").GetInt32());
    }

    [Fact]
    public async Task The_seat_gauge_only_tracks_recent_shows()
    {
        var api = _db.Api;
        var client = await api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 2));
        var gauge = api.Services.GetRequiredService<SeatGauge>();
        await gauge.RefreshAsync(CancellationToken.None);
        Assert.Contains($"seatres_seats{{show=\"{show}\"", await client.GetStringAsync("/metrics"));

        api.Clock.Advance(SeatGauge.Window + TimeSpan.FromDays(1));
        await gauge.RefreshAsync(CancellationToken.None);
        Assert.DoesNotContain($"seatres_seats{{show=\"{show}\"", await client.GetStringAsync("/metrics"));
    }
}
