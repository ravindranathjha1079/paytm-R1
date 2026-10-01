using Npgsql;
using SeatRes.Api.Data;

namespace SeatRes.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

        // Fails closed: not ready until migrations applied and the DB answers within one second.
        app.MapGet("/health/ready", async (NpgsqlDataSource ds, MigrationState state, CancellationToken ct) =>
        {
            if (!state.Applied)
                return Results.Json(new { status = "not_ready", reason = "migrations_pending" }, statusCode: 503);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(1));
                await using var cmd = ds.CreateCommand("SELECT 1");
                await cmd.ExecuteScalarAsync(cts.Token);
                return Results.Ok(new { status = "ready" });
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return Results.Json(new { status = "not_ready", reason = "db_unreachable" }, statusCode: 503);
            }
        });
    }
}
