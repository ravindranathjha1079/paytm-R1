using System.Net;
using SeatRes.IntegrationTests.Infrastructure;

namespace SeatRes.IntegrationTests;

public class HealthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Liveness_is_200_without_any_dependency()
    {
        var res = await factory.CreateClient().GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Every_response_carries_a_generated_request_id()
    {
        var res = await factory.CreateClient().GetAsync("/health/live");
        var id = Assert.Single(res.Headers.GetValues("X-Request-Id"));
        Assert.Matches("^[a-f0-9]{32}$", id);
    }

    [Fact]
    public async Task A_valid_incoming_request_id_is_echoed()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        req.Headers.Add("X-Request-Id", "burst-42.a_b");
        var res = await factory.CreateClient().SendAsync(req);
        Assert.Equal("burst-42.a_b", Assert.Single(res.Headers.GetValues("X-Request-Id")));
    }

    [Fact]
    public async Task An_invalid_incoming_request_id_is_replaced()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        req.Headers.TryAddWithoutValidation("X-Request-Id", "bad id!");
        var res = await factory.CreateClient().SendAsync(req);
        Assert.NotEqual("bad id!", Assert.Single(res.Headers.GetValues("X-Request-Id")));
    }

    [Fact]
    public async Task Metrics_endpoint_exposes_the_service_catalogue()
    {
        var body = await factory.CreateClient().GetStringAsync("/metrics");
        Assert.Contains("seatres_unhandled_errors_total", body);
        Assert.Contains("seatres_reservations_declined_total", body);
    }

    [Fact]
    public async Task Unknown_route_is_404()
    {
        var res = await factory.CreateClient().GetAsync("/nope");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}

public class OpenApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task The_api_describes_itself()
    {
        var res = await factory.CreateClient().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var doc = await res.Content.ReadAsStringAsync();
        Assert.Contains("/shows/{id}/reserve", doc);
        Assert.Contains("/reservations/{id}/confirm", doc);
    }
}
