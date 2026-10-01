using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SeatRes.IntegrationTests.Infrastructure;

public static class TestClients
{
    private static readonly ConcurrentDictionary<string, string> Tokens = new();
    private static int _seq;

    public static string NewUser(string prefix = "u") => $"{prefix}{Interlocked.Increment(ref _seq)}-{Guid.NewGuid():N}"[..20];

    public static async Task<string> TokenAsync(this HttpClient client, string userId)
    {
        if (Tokens.TryGetValue(userId, out var cached)) return cached;
        var res = await client.PostAsJsonAsync("/auth/token", new { user_id = userId });
        res.EnsureSuccessStatusCode();
        var token = (await res.JsonAsync()).GetProperty("token").GetString()!;
        return Tokens[userId] = token;
    }

    public static async Task<string[]> TokensAsync(this HttpClient client, int count, string prefix = "u") =>
        await Task.WhenAll(Enumerable.Range(0, count).Select(_ => client.TokenAsync(NewUser(prefix))));

    public static async Task<string> AdminTokenAsync(this HttpClient client)
    {
        if (Tokens.TryGetValue("__admin", out var cached)) return cached;
        var req = new HttpRequestMessage(HttpMethod.Post, "/auth/admin-token");
        req.Headers.Add("X-Admin-Key", TestKeys.Admin);
        var res = await client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return Tokens["__admin"] = (await res.JsonAsync()).GetProperty("token").GetString()!;
    }

    public static string[] Seats(string row, int count) =>
        Enumerable.Range(1, count).Select(i => $"{row}{i}").ToArray();

    public static async Task<Guid> CreateShowAsync(this HttpClient client, string[] seats, long pricePaise = 25_000,
        int? perUserLimit = null)
    {
        var res = await client.SendJsonAsync(HttpMethod.Post, "/shows",
            new { name = "show-" + Guid.NewGuid().ToString("N")[..8], seats, price_paise = pricePaise, per_user_limit = perUserLimit },
            await client.AdminTokenAsync());
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(await res.Content.ReadAsStringAsync());
        return (await res.JsonAsync()).GetProperty("id").GetGuid();
    }

    public static Task<HttpResponseMessage> SendJsonAsync(this HttpClient client, HttpMethod method, string url, object? body,
        string? token, IDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null)
            req.Content = body is string raw
                ? new StringContent(raw, Encoding.UTF8, "application/json")
                : JsonContent.Create(body);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var (k, v) in headers ?? new Dictionary<string, string>()) req.Headers.TryAddWithoutValidation(k, v);
        return client.SendAsync(req, ct);
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<JsonElement> ShowAsync(this HttpClient client, Guid showId)
    {
        var res = await client.GetAsync($"/shows/{showId}");
        res.EnsureSuccessStatusCode();
        return await res.JsonAsync();
    }

    public static string Error(this JsonElement body) => body.GetProperty("error").GetString()!;
}
