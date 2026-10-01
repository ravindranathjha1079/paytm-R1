namespace SeatRes.Api;

/// <summary>`dotnet SeatRes.Api.dll healthcheck` — used by the container HEALTHCHECK (the runtime image has no curl).</summary>
public static class HealthProbe
{
    public static async Task<int> RunAsync()
    {
        var ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080";
        var port = ports.Split(';', ',')[0].Trim();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            var res = await http.GetAsync($"http://127.0.0.1:{port}/health/ready");
            return res.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }
}
