using System.Net;
using System.Net.Http.Json;
using SeatRes.IntegrationTests.Infrastructure;

namespace SeatRes.IntegrationTests;

[Collection("db")]
public class AuthTests(PostgresFixture db)
{
    [Fact]
    public async Task A_minted_token_identifies_its_user()
    {
        var client = await db.Api.ReadyClientAsync();
        var user = TestClients.NewUser();
        var me = await client.SendJsonAsync(HttpMethod.Get, "/me", null, await client.TokenAsync(user));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await me.JsonAsync();
        Assert.Equal(user, body.GetProperty("user_id").GetString());
        Assert.Equal("user", body.GetProperty("role").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("x;drop")]
    public async Task Invalid_user_ids_are_rejected(string userId)
    {
        var client = await db.Api.ReadyClientAsync();
        var res = await client.PostAsJsonAsync("/auth/token", new { user_id = userId });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Wrong_admin_key_is_401()
    {
        var client = await db.Api.ReadyClientAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "/auth/admin-token");
        req.Headers.Add("X-Admin-Key", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Missing_or_forged_token_is_401_json()
    {
        var client = await db.Api.ReadyClientAsync();
        var none = await client.GetAsync("/me");
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal("unauthorized", (await none.JsonAsync()).Error());

        var forged = await client.SendJsonAsync(HttpMethod.Get, "/me", null,
            "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ2aWN0aW0ifQ.c2lnbmF0dXJl");
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }
}
