using System.Net;
using SeatRes.IntegrationTests.Infrastructure;

namespace SeatRes.IntegrationTests;

[Collection("db")]
public class ShowTests(PostgresFixture db)
{
    [Fact]
    public async Task Created_show_has_every_seat_available_in_order()
    {
        var client = await db.Api.ReadyClientAsync();
        var res = await client.SendJsonAsync(HttpMethod.Post, "/shows",
            new { name = "friday-night", seats = new[] { "A2", "A1", "B10" }, price_paise = 25_000 },
            await client.AdminTokenAsync());
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.JsonAsync();
        Assert.Equal(4, body.GetProperty("per_user_limit").GetInt32());
        Assert.Equal(3, body.GetProperty("total_seats").GetInt32());
        var seats = body.GetProperty("seats").EnumerateArray().ToList();
        Assert.Equal(["A2", "A1", "B10"], seats.Select(s => s.GetProperty("label").GetString()));
        Assert.All(seats, s => Assert.Equal("available", s.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Show_state_reports_counts_that_reconcile()
    {
        var client = await db.Api.ReadyClientAsync();
        var id = await client.CreateShowAsync(TestClients.Seats("A", 5), perUserLimit: 2);
        var show = await client.ShowAsync(id);
        var counts = show.GetProperty("counts");
        Assert.Equal(5, counts.GetProperty("available").GetInt32());
        Assert.Equal(0, counts.GetProperty("held").GetInt32());
        Assert.Equal(0, counts.GetProperty("confirmed").GetInt32());
        Assert.True(show.GetProperty("reconciled").GetBoolean());
        Assert.Equal(2, show.GetProperty("per_user_limit").GetInt32());
        Assert.Equal(0, show.GetProperty("ledger").GetProperty("net_paise").GetInt64());
    }

    [Fact]
    public async Task Creating_a_show_requires_the_admin_role()
    {
        var client = await db.Api.ReadyClientAsync();
        var body = new { name = "x", seats = new[] { "A1" }, price_paise = 100 };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendJsonAsync(HttpMethod.Post, "/shows", body, null)).StatusCode);
        var asUser = await client.SendJsonAsync(HttpMethod.Post, "/shows", body, await client.TokenAsync(TestClients.NewUser()));
        Assert.Equal(HttpStatusCode.Forbidden, asUser.StatusCode);
        Assert.Equal("forbidden", (await asUser.JsonAsync()).Error());
    }

    public static TheoryData<string> InvalidShows => new()
    {
        """{"name":"x","seats":[],"price_paise":100}""",
        """{"name":"x","seats":["A1","A1"],"price_paise":100}""",
        """{"name":"x","seats":["A 1"],"price_paise":100}""",
        """{"name":"","seats":["A1"],"price_paise":100}""",
        """{"name":"x","seats":["A1"],"price_paise":-1}""",
        """{"name":"x","seats":["A1"],"price_paise":250.5}""",
        """{"name":"x","seats":["A1"]}""",
        """{"name":"x","seats":["A1"],"price_paise":100,"per_user_limit":0}""",
        """{"name":"x","seats":"A1","price_paise":100}""",
        """not json""",
    };

    [Theory]
    [MemberData(nameof(InvalidShows))]
    public async Task Invalid_shows_are_400(string json)
    {
        var client = await db.Api.ReadyClientAsync();
        var res = await client.SendJsonAsync(HttpMethod.Post, "/shows", json, await client.AdminTokenAsync());
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.False(string.IsNullOrEmpty((await res.JsonAsync()).Error()));
    }

    [Fact]
    public async Task Too_many_seats_is_400()
    {
        var client = await db.Api.ReadyClientAsync();
        var res = await client.SendJsonAsync(HttpMethod.Post, "/shows",
            new { name = "huge", seats = TestClients.Seats("Z", 20_001), price_paise = 1 }, await client.AdminTokenAsync());
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_show_is_404()
    {
        var client = await db.Api.ReadyClientAsync();
        var res = await client.GetAsync($"/shows/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("not_found", (await res.JsonAsync()).Error());
    }
}
