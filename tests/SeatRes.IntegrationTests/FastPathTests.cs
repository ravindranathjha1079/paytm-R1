using System.Globalization;
using System.Net;
using SeatRes.Api.Payments;
using SeatRes.IntegrationTests.Infrastructure;

namespace SeatRes.IntegrationTests;

/// <summary>Fast path enabled: the in-memory layers may only decline or delay, never change an outcome.</summary>
[Collection("db")]
public class FastPathTests(PostgresFixture db)
{
    private DbApiFactory Api => db.FastApi;

    private static double FastDeclines(string metrics) => metrics.Split('\n')
        .Where(l => l.StartsWith("seatres_fast_path_declines_total{"))
        .Sum(l => double.Parse(l.Split(' ')[^1], CultureInfo.InvariantCulture));

    [Fact]
    public async Task Hot_seat_storm_still_has_exactly_one_winner_and_most_losers_skip_the_db()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 20));
        var tokens = await client.TokensAsync(500, "fast");
        var before = FastDeclines(await client.GetStringAsync("/metrics"));

        var responses = await Task.WhenAll(tokens.Select(t => Reserve.PostAsync(client, t, show, ["A7"])));

        Invariants.NoServerErrors(responses);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(499, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.True(FastDeclines(await client.GetStringAsync("/metrics")) - before > 0);
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Other_users_storming_a_taken_seat_are_declined_without_touching_the_database()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A3"])).Status);
        var tokens = await client.TokensAsync(200, "nodb");
        double Lookups(string m) => m.Split('\n').Where(l => l.StartsWith("seatres_idempotency_lookups_total"))
            .Sum(l => double.Parse(l.Split(' ')[^1], CultureInfo.InvariantCulture));
        var metricsBefore = await client.GetStringAsync("/metrics");
        Assert.Contains("seatres_idempotency_lookups_total", metricsBefore);
        var before = Lookups(metricsBefore);

        var responses = await Task.WhenAll(tokens.Select(t => Reserve.PostAsync(client, t, show, ["A3"])));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(before, Lookups(await client.GetStringAsync("/metrics")));
    }

    [Fact]
    public async Task Retrying_the_winning_key_replays_the_reservation_instead_of_seat_taken()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        var token = await client.TokenAsync(TestClients.NewUser());
        var (first, body) = await Reserve.SendAsync(client, token, show, ["A1"], "win-1");
        var (again, replay) = await Reserve.SendAsync(client, token, show, ["A1"], "win-1");
        Assert.Equal(HttpStatusCode.Created, first);
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.Equal(body.GetProperty("reservation_id").GetGuid(), replay.GetProperty("reservation_id").GetGuid());
    }

    [Fact]
    public async Task Concurrent_retries_of_the_winning_key_all_replay_even_when_the_cache_knows_the_seat_is_taken()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        var token = await client.TokenAsync(TestClients.NewUser());
        var key = Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Reserve.PostAsync(client, token, show, ["A1"], key)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(49, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
    }

    [Fact]
    public async Task A_cancelled_seat_is_rebookable_immediately()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        var token = await client.TokenAsync(TestClients.NewUser());
        var (_, hold) = await Reserve.SendAsync(client, token, show, ["A1"]);
        Assert.Equal(HttpStatusCode.Conflict, (await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"])).Status);

        await client.SendJsonAsync(HttpMethod.Post, $"/reservations/{hold.GetProperty("reservation_id").GetGuid()}/cancel", null, token);

        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"])).Status);
    }

    [Fact]
    public async Task An_expired_hold_is_claimable_despite_the_cache()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"]);
        Api.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.Created, (await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"])).Status);
    }
}

[Collection("db")]
public class AdmissionTests(PostgresFixture db)
{
    [Fact]
    public async Task Beyond_capacity_writes_get_a_fast_429_not_a_5xx()
    {
        await using var api = new DbApiFactory(db.ConnectionString, fastPath: true, new Dictionary<string, string>
        {
            ["SeatRes:AdmissionPermits"] = "1",
            ["SeatRes:AdmissionQueue"] = "0",
        });
        var client = await api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        api.Gateway.Plan(Script.Block);
        var busy = Reserve.PostAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"], confirm: true);
        await api.Gateway.Entered;

        var rejected = await Reserve.PostAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A2"]);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("1", rejected.Headers.GetValues("Retry-After").Single());
        Assert.Equal("overloaded", (await rejected.JsonAsync()).Error());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        api.Gateway.Release(GatewayStatus.Succeeded);
        Assert.Equal(HttpStatusCode.Created, (await busy).StatusCode);
    }
}
