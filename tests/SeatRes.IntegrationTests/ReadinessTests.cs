using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SeatRes.Api.Data;
using SeatRes.IntegrationTests.Infrastructure;

namespace SeatRes.IntegrationTests;

[Collection("db")]
public class ReadinessTests(PostgresFixture db)
{
    [Fact]
    public async Task Ready_once_migrations_have_applied()
    {
        var client = await db.Api.ReadyClientAsync();
        var res = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Migrations_are_idempotent()
    {
        await db.Api.ReadyClientAsync();
        var migrator = db.Api.Services.GetRequiredService<Migrator>();
        await migrator.ApplyAsync(CancellationToken.None);
        await migrator.ApplyAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Readiness_fails_closed_when_the_database_goes_away()
    {
        var container = PostgresFixture.NewContainer();
        await container.StartAsync();
        await using var api = new DbApiFactory(container.GetConnectionString() + ";Timeout=2", fastPath: false);
        var client = await api.ReadyClientAsync();

        await container.StopAsync();

        var started = DateTime.UtcNow;
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "readiness must answer within its budget");
        var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("db_unreachable", body.GetProperty("reason").GetString());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        await container.DisposeAsync();
    }
}
