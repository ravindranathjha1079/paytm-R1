using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SeatRes.IntegrationTests.Infrastructure;

public static class TestKeys
{
    public const string Jwt = "integration-test-signing-key-0123456789abcdef";
    public const string Admin = "integration-admin-key";
}

/// <summary>Hosts the API in-process. Database-backed variants are added in later tasks.</summary>
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
    }
}
