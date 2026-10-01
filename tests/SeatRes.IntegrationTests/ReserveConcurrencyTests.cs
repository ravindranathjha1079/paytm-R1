using System.Net;
using SeatRes.IntegrationTests.Infrastructure;

namespace SeatRes.IntegrationTests;

/// <summary>Fast path disabled: these prove the database decision alone is race-free.</summary>
[Collection("db")]
public class ReserveConcurrencyTests(PostgresFixture db)
{
    private DbApiFactory Api => db.Api;

    [Fact]
    public async Task A_single_reserve_creates_a_five_minute_hold()
    {
        var client = await Api.ReadyClientAsync();
        var user = TestClients.NewUser();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3), pricePaise: 25_000);
        var (status, body) = await Reserve.SendAsync(client, await client.TokenAsync(user), show, ["A2"]);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("held", body.GetProperty("status").GetString());
        Assert.Equal(user, body.GetProperty("user_id").GetString());
        Assert.Equal(show, body.GetProperty("show_id").GetGuid());
        Assert.Equal(["A2"], Reserve.SeatsOf(body));
        Assert.Equal(25_000, body.GetProperty("amount_paise").GetInt64());
        var serverTime = body.GetProperty("server_time").GetDateTime();
        Assert.Equal(serverTime.AddMinutes(5), body.GetProperty("expires_at").GetDateTime());

        var state = await client.ShowAsync(show);
        Assert.Equal(1, state.GetProperty("counts").GetProperty("held").GetInt32());
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Five_hundred_users_storming_one_seat_produce_exactly_one_winner()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 20));
        var tokens = await client.TokensAsync(500, "storm");

        var responses = await Task.WhenAll(tokens.Select(t => Reserve.PostAsync(client, t, show, ["A12"])));

        Invariants.NoServerErrors(responses);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var losers = responses.Where(r => r.StatusCode != HttpStatusCode.Created).ToList();
        Assert.Equal(499, losers.Count);
        Assert.All(losers, r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        foreach (var r in losers)
            Assert.Equal("seat_taken", (await r.JsonAsync()).Error());
        Assert.Equal(1, (await client.ShowAsync(show)).GetProperty("counts").GetProperty("held").GetInt32());
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Ten_users_on_the_same_two_seats_one_gets_both()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        var tokens = await client.TokensAsync(10);

        var responses = await Task.WhenAll(tokens.Select(t => Reserve.PostAsync(client, t, show, ["A1", "A2"])));

        Invariants.NoServerErrors(responses);
        var winner = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(["A1", "A2"], Reserve.SeatsOf(await winner.JsonAsync()));
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(2, (await client.ShowAsync(show)).GetProperty("counts").GetProperty("held").GetInt32());
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Overlapping_group_and_single_seat_requests_never_split_a_group()
    {
        var client = await Api.ReadyClientAsync();
        for (var trial = 0; trial < 20; trial++)
        {
            var show = await client.CreateShowAsync(["A1", "A2", "A3", "A4"]);
            var tokens = await client.TokensAsync(3, "grp");
            var requests = new[]
            {
                (Token: tokens[0], Seats: new[] { "A1", "A2", "A3" }),
                (Token: tokens[1], Seats: new[] { "A1", "A2", "A3" }),
                (Token: tokens[2], Seats: new[] { "A2" }),
            };
            var responses = await Task.WhenAll(requests.Select(r => Reserve.PostAsync(client, r.Token, show, r.Seats)));

            Invariants.NoServerErrors(responses);
            var winners = responses.Where(r => r.StatusCode == HttpStatusCode.Created).ToList();
            Assert.Single(winners);
            var won = Reserve.SeatsOf(await winners[0].JsonAsync());
            var held = (await client.ShowAsync(show)).GetProperty("seats").EnumerateArray()
                .Where(s => s.GetProperty("status").GetString() == "held")
                .Select(s => s.GetProperty("label").GetString()).ToArray();
            Assert.Equal(won, held);
            await Invariants.AssertAsync(Api, client, show);
        }
    }

    [Fact]
    public async Task Random_overlapping_sets_never_deadlock_or_double_sell()
    {
        var client = await Api.ReadyClientAsync();
        var seats = TestClients.Seats("A", 6);
        var show = await client.CreateShowAsync(seats);
        var tokens = await client.TokensAsync(50, "mix");
        var rng = new Random(7);
        var wanted = tokens.Select(_ => seats.OrderBy(_ => rng.Next()).Take(rng.Next(1, 4)).ToArray()).ToArray();

        var responses = await Task.WhenAll(tokens.Select((t, i) => Reserve.PostAsync(client, t, show, wanted[i])));

        Invariants.NoServerErrors(responses);
        var wonSeats = new List<string>();
        foreach (var r in responses.Where(r => r.StatusCode == HttpStatusCode.Created))
            wonSeats.AddRange(Reserve.SeatsOf(await r.JsonAsync()));
        Assert.Equal(wonSeats.Count, wonSeats.Distinct().Count());
        Assert.Equal(wonSeats.Count, (await client.ShowAsync(show)).GetProperty("counts").GetProperty("held").GetInt32());
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task One_user_firing_ten_parallel_reserves_gets_at_most_the_limit()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 10), perUserLimit: 4);
        var token = await client.TokenAsync(TestClients.NewUser());

        var responses = await Task.WhenAll(Enumerable.Range(1, 10)
            .Select(i => Reserve.PostAsync(client, token, show, [$"A{i}"])));

        Invariants.NoServerErrors(responses);
        Assert.Equal(4, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        var declined = responses.Where(r => r.StatusCode != HttpStatusCode.Created).ToList();
        Assert.Equal(6, declined.Count);
        foreach (var r in declined)
            Assert.Equal("per_user_limit", (await r.JsonAsync()).Error());
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task Asking_for_more_seats_than_the_limit_in_one_request_is_declined()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 10), perUserLimit: 4);
        var (status, body) = await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show,
            TestClients.Seats("A", 5));
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("per_user_limit", body.Error());
        Assert.Equal(10, (await client.ShowAsync(show)).GetProperty("counts").GetProperty("available").GetInt32());
    }

    [Fact]
    public async Task Partially_available_multi_seat_request_is_all_or_nothing()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A3"]);

        var (status, body) = await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A2", "A3"]);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("seat_taken", body.Error());
        Assert.Equal(["A3"], body.GetProperty("taken").EnumerateArray().Select(x => x.GetString()));
        var a2 = (await client.ShowAsync(show)).GetProperty("seats").EnumerateArray().Single(s => s.GetProperty("label").GetString() == "A2");
        Assert.Equal("available", a2.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Seat_already_yours_is_reported_as_held_by_you()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        var token = await client.TokenAsync(TestClients.NewUser());
        await Reserve.SendAsync(client, token, show, ["A1"]);
        var (status, body) = await Reserve.SendAsync(client, token, show, ["A1"]);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.True(body.GetProperty("held_by_you").GetBoolean());
    }

    [Fact]
    public async Task Identity_comes_from_the_token_not_the_body()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3));
        var me = TestClients.NewUser();
        var res = await Reserve.PostAsync(client, await client.TokenAsync(me), show, ["A1"], spoofUserId: "victim");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal(me, (await res.JsonAsync()).GetProperty("user_id").GetString());
    }
}
