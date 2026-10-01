using System.Net;
using SeatRes.IntegrationTests.Infrastructure;

namespace SeatRes.IntegrationTests;

[Collection("db")]
public class ReserveValidationTests(PostgresFixture db)
{
    private DbApiFactory Api => db.Api;

    private async Task<(HttpStatusCode, string)> Post(object body, IDictionary<string, string>? headers = null, bool auth = true)
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 5));
        var token = auth ? await client.TokenAsync(TestClients.NewUser()) : null;
        var res = await client.SendJsonAsync(HttpMethod.Post, $"/shows/{show}/reserve", body, token, headers);
        var json = await res.JsonAsync();
        return (res.StatusCode, json.ValueKind == System.Text.Json.JsonValueKind.Object && json.TryGetProperty("error", out var e) ? e.GetString()! : "");
    }

    [Fact]
    public async Task Empty_seat_list_is_400() =>
        Assert.Equal((HttpStatusCode.BadRequest, "bad_request"), await Post(new { seats = Array.Empty<string>(), idempotency_key = "k" }));

    [Fact]
    public async Task Duplicate_seats_are_400() =>
        Assert.Equal((HttpStatusCode.BadRequest, "bad_request"), await Post(new { seats = new[] { "A1", "A1" }, idempotency_key = "k" }));

    [Fact]
    public async Task Unknown_seats_are_400() =>
        Assert.Equal((HttpStatusCode.BadRequest, "unknown_seats"), await Post(new { seats = new[] { "Z9" }, idempotency_key = "k" }));

    [Fact]
    public async Task Labels_are_case_sensitive() =>
        Assert.Equal((HttpStatusCode.BadRequest, "unknown_seats"), await Post(new { seats = new[] { "a1" }, idempotency_key = "k" }));

    [Fact]
    public async Task Oversized_requests_are_rejected_before_touching_the_db() =>
        Assert.Equal((HttpStatusCode.BadRequest, "bad_request"),
            await Post(new { seats = TestClients.Seats("A", 65), idempotency_key = "k" }));

    [Fact]
    public async Task Missing_idempotency_key_is_400() =>
        Assert.Equal((HttpStatusCode.BadRequest, "bad_request"), await Post(new { seats = new[] { "A1" } }));

    [Fact]
    public async Task Conflicting_header_and_body_keys_are_400() =>
        Assert.Equal((HttpStatusCode.BadRequest, "bad_request"),
            await Post(new { seats = new[] { "A1" }, idempotency_key = "a" }, new Dictionary<string, string> { ["Idempotency-Key"] = "b" }));

    [Fact]
    public async Task No_token_is_401() =>
        Assert.Equal((HttpStatusCode.Unauthorized, "unauthorized"), await Post(new { seats = new[] { "A1" }, idempotency_key = "k" }, auth: false));

    [Fact]
    public async Task Malformed_json_is_400() =>
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("{\"seats\": [")).Item1);

    [Fact]
    public async Task Unknown_show_is_404()
    {
        var client = await Api.ReadyClientAsync();
        var res = await Reserve.PostAsync(client, await client.TokenAsync(TestClients.NewUser()), Guid.NewGuid(), ["A1"]);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
