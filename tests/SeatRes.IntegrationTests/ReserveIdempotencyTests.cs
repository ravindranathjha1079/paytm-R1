using System.Net;
using SeatRes.IntegrationTests.Infrastructure;

namespace SeatRes.IntegrationTests;

[Collection("db")]
public class ReserveIdempotencyTests(PostgresFixture db)
{
    private DbApiFactory Api => db.Api;

    [Fact]
    public async Task Concurrent_retries_with_the_same_key_reserve_exactly_once()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        var token = await client.TokenAsync(TestClients.NewUser());
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Reserve.PostAsync(client, token, show, ["A1"], key)));

        Invariants.NoServerErrors(responses);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var replays = responses.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
        Assert.Equal(19, replays.Count);
        Assert.All(replays, r => Assert.Equal("true", r.Headers.GetValues("Idempotent-Replayed").Single()));
        var ids = new HashSet<Guid>();
        foreach (var r in responses) ids.Add((await r.JsonAsync()).GetProperty("reservation_id").GetGuid());
        Assert.Single(ids);
        Assert.Equal(1, (await client.ShowAsync(show)).GetProperty("counts").GetProperty("held").GetInt32());
    }

    [Fact]
    public async Task Same_key_with_different_seats_is_a_409()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        var token = await client.TokenAsync(TestClients.NewUser());
        await Reserve.SendAsync(client, token, show, ["A1"], "k-1");

        var (status, body) = await Reserve.SendAsync(client, token, show, ["A2"], "k-1");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("idempotency_mismatch", body.Error());
        Assert.Equal(1, (await client.ShowAsync(show)).GetProperty("counts").GetProperty("held").GetInt32());
    }

    [Fact]
    public async Task Seat_order_does_not_make_a_retry_a_mismatch()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        var token = await client.TokenAsync(TestClients.NewUser());
        await Reserve.SendAsync(client, token, show, ["A1", "A2"], "k-ord");
        var (status, _) = await Reserve.SendAsync(client, token, show, ["A2", "A1"], "k-ord");
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task A_declined_request_replays_the_same_decline()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 2));
        await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"]);
        var token = await client.TokenAsync(TestClients.NewUser());

        var first = await Reserve.SendAsync(client, token, show, ["A1"], "k-decl");
        var again = await Reserve.SendAsync(client, token, show, ["A1"], "k-decl");

        Assert.Equal(HttpStatusCode.Conflict, first.Status);
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
        Assert.Equal("seat_taken", again.Body.Error());
    }

    [Fact]
    public async Task Keys_are_scoped_per_user()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        var a = await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A1"], "shared");
        var b = await Reserve.SendAsync(client, await client.TokenAsync(TestClients.NewUser()), show, ["A2"], "shared");
        Assert.Equal(HttpStatusCode.Created, a.Status);
        Assert.Equal(HttpStatusCode.Created, b.Status);
    }

    [Fact]
    public async Task Key_in_header_is_honoured()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        var token = await client.TokenAsync(TestClients.NewUser());
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = "hdr-1" };
        var first = await client.SendJsonAsync(HttpMethod.Post, $"/shows/{show}/reserve", new { seats = new[] { "A1" } }, token, headers);
        var second = await client.SendJsonAsync(HttpMethod.Post, $"/shows/{show}/reserve", new { seats = new[] { "A1" } }, token, headers);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }
}
