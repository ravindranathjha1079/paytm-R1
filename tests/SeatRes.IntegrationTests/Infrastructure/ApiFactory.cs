using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace SeatRes.IntegrationTests.Infrastructure;

public static class TestKeys
{
    public const string Jwt = "integration-test-signing-key-0123456789abcdef";
    public const string Admin = "integration-admin-key";
}

/// <summary>Hosts the API in-process with no reachable database (for dependency-free endpoints).</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    protected virtual string ConnectionString => "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Db", ConnectionString);
        builder.UseSetting("Auth:JwtKey", TestKeys.Jwt);
        builder.UseSetting("Auth:AdminKey", TestKeys.Admin);
        builder.UseSetting("SeatRes:BackgroundEnabled", "false");
        ConfigureMore(builder);
    }

    protected virtual void ConfigureMore(IWebHostBuilder builder) { }
}

/// <summary>Hosts the API against a real Postgres with a controllable clock.</summary>
public class DbApiFactory(string connectionString, bool fastPath, IDictionary<string, string>? settings = null) : ApiFactory
{
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero));

    protected override string ConnectionString => connectionString;

    protected override void ConfigureMore(IWebHostBuilder builder)
    {
        builder.UseSetting("SeatRes:FastPathEnabled", fastPath.ToString());
        foreach (var (k, v) in settings ?? new Dictionary<string, string>())
            builder.UseSetting(k, v);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public async Task<HttpClient> ReadyClientAsync()
    {
        var client = CreateClient();
        for (var i = 0; i < 100; i++)
        {
            if ((await client.GetAsync("/health/ready")).StatusCode == HttpStatusCode.OK) return client;
            await Task.Delay(100);
        }
        throw new TimeoutException("API never became ready");
    }
}
