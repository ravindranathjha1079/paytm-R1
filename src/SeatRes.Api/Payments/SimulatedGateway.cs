using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace SeatRes.Api.Payments;

/// <summary>
/// Simulated provider with its own book (gateway_charges), written outside our transactions just as an
/// external system would be. Knobs: latency, decline rate, lost-response rate, and an optional per-request
/// hint (X-Sim-Gateway: decline | unknown | slow) so a burst can drive every failure path on demand.
/// "unknown" charges the card but loses the response; "slow" charges after our client timeout has fired.
/// </summary>
public sealed class SimulatedGateway(GatewayDataSource gatewayDb, IOptions<GatewayOptions> options, IOptions<SeatResOptions> seatres)
    : IPaymentGateway
{
    private NpgsqlDataSource ds => gatewayDb.Source;

    public async Task<GatewayStatus> ChargeAsync(string key, long amountPaise, string? hint, CancellationToken ct)
    {
        var o = options.Value;
        var mode = o.AllowClientOverride ? hint?.Trim().ToLowerInvariant() : null;
        var delay = mode == "slow" ? seatres.Value.GatewayTimeoutMillis + 2000 : o.LatencyMillis;
        if (delay > 0) await Task.Delay(delay, CancellationToken.None);

        var existing = await QueryAsync(key, CancellationToken.None);
        if (existing != GatewayStatus.NotFound) return existing;

        var roll = Random.Shared.NextDouble();
        var declined = mode == "decline" || (mode is null && roll < o.DeclineRate);
        var loseResponse = mode == "unknown" || (mode is null && !declined && roll < o.DeclineRate + o.UnknownRate);

        await using var c = await ds.OpenConnectionAsync(CancellationToken.None);
        await c.ExecuteAsync(
            """
            INSERT INTO gateway_charges (gateway_key, amount_paise, status, created_at)
            VALUES (@key, @amountPaise, @status, now()) ON CONFLICT (gateway_key) DO NOTHING
            """,
            new { key, amountPaise, status = declined ? "declined" : "succeeded" });
        var recorded = await QueryAsync(key, CancellationToken.None);
        return loseResponse ? GatewayStatus.Unknown : recorded;
    }

    public async Task<GatewayStatus> QueryAsync(string key, CancellationToken ct)
    {
        await using var c = await ds.OpenConnectionAsync(ct);
        var status = await c.ExecuteScalarAsync<string?>("SELECT status FROM gateway_charges WHERE gateway_key = @key", new { key });
        return status switch
        {
            "succeeded" => GatewayStatus.Succeeded,
            "declined" => GatewayStatus.Declined,
            _ => GatewayStatus.NotFound,
        };
    }

    public async Task<bool> RefundAsync(string key, CancellationToken ct)
    {
        await using var c = await ds.OpenConnectionAsync(ct);
        var rows = await c.ExecuteAsync(
            "UPDATE gateway_charges SET refunded = true WHERE gateway_key = @key AND status = 'succeeded'", new { key });
        return rows == 1;
    }
}

/// <summary>
/// The "external" provider gets its own small pool, as a real one would live elsewhere: payment calls can
/// never queue behind (or starve) the reservation path's 30 connections.
/// </summary>
public sealed class GatewayDataSource(IConfiguration config) : IDisposable
{
    private readonly Lazy<NpgsqlDataSource> _source = new(() =>
        new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(config.GetConnectionString("Db")) { MaxPoolSize = 5 }.ConnectionString)
            { Name = "gateway" }.Build());

    public NpgsqlDataSource Source => _source.Value;

    public void Dispose()
    {
        if (_source.IsValueCreated) _source.Value.Dispose();
    }
}
