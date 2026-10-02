using System.Net;
using SeatRes.Api.Payments;
using SeatRes.IntegrationTests.Infrastructure;

namespace SeatRes.IntegrationTests;

[Collection("db")]
public class FreeShowTests : IDisposable
{
    private readonly PostgresFixture _db;
    private DbApiFactory Api => _db.Api;

    public FreeShowTests(PostgresFixture db)
    {
        _db = db;
        db.Api.Gateway.Reset();
    }

    public void Dispose() => Api.Gateway.Release(GatewayStatus.Declined);

    [Fact]
    public async Task A_free_show_can_be_confirmed_and_cancelled_without_a_ledger_entry()
    {
        var client = await Api.ReadyClientAsync();
        var show = await client.CreateShowAsync(TestClients.Seats("A", 3), pricePaise: 0);
        var token = await client.TokenAsync(TestClients.NewUser());
        var (_, hold) = await Reserve.SendAsync(client, token, show, ["A1"]);
        var id = hold.GetProperty("reservation_id").GetGuid();

        var confirm = await client.SendJsonAsync(HttpMethod.Post, $"/reservations/{id}/confirm", null, token,
            new Dictionary<string, string> { ["Idempotency-Key"] = "free-1" });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var (status, _) = await Reserve.SendAsync(client, token, show, ["A2"], confirm: true);
        Assert.Equal(HttpStatusCode.Created, status);

        var cancel = await client.SendJsonAsync(HttpMethod.Post, $"/reservations/{id}/cancel", null, token);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        await Invariants.AssertAsync(Api, client, show);
    }

    [Fact]
    public async Task An_absurd_price_is_rejected_at_creation_not_overflowed_later()
    {
        var client = await Api.ReadyClientAsync();
        var res = await client.SendJsonAsync(HttpMethod.Post, "/shows",
            new { name = "x", seats = new[] { "A1" }, price_paise = long.MaxValue / 2 }, await client.AdminTokenAsync());
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
