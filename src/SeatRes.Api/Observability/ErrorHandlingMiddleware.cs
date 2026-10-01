using System.Text.Json;
using Npgsql;
using SeatRes.Api.Domain;

namespace SeatRes.Api.Observability;

/// <summary>
/// Last line of defence: maps contention and overload to 4xx, an unreachable DB to 503,
/// and only genuinely unexpected failures to 500 (counted and logged at Error).
/// </summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> log)
{
    private static readonly HashSet<string> TransientSqlStates = ["40P01", "40001", "55P03", "57014"];

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // Client went away; nothing to answer.
        }
        catch (Exception ex) when (!ctx.Response.HasStarted)
        {
            var (status, code) = Classify(ex);
            if (code == ErrorCodes.Internal)
            {
                SeatResMetrics.UnhandledErrors.Inc();
                log.LogError(ex, "unhandled_error {path}", ctx.Request.Path.Value);
            }
            else
            {
                log.LogWarning("request_rejected {code} {exception_type} {exception_message}",
                    code, ex.GetType().Name, ex.Message);
            }

            if (status == 429)
            {
                ctx.Response.Headers.RetryAfter = "1";
                SeatResMetrics.Declined.WithLabels(ErrorCodes.Overloaded).Inc();
            }

            await ApiResult.Fail(status, code, MessageFor(code)).WriteAsync(ctx);
        }
    }

    internal static (int Status, string Code) Classify(Exception ex) => ex switch
    {
        BadHttpRequestException or JsonException => (400, ErrorCodes.BadRequest),
        PostgresException pg when TransientSqlStates.Contains(pg.SqlState) => (429, ErrorCodes.Overloaded),
        PostgresException => (500, ErrorCodes.Internal),
        NpgsqlException n when n.Message.Contains("connection pool has been exhausted", StringComparison.OrdinalIgnoreCase)
            => (429, ErrorCodes.Overloaded),
        NpgsqlException => (503, ErrorCodes.DbUnavailable),
        _ => (500, ErrorCodes.Internal),
    };

    private static string MessageFor(string code) => code switch
    {
        ErrorCodes.BadRequest => "malformed request",
        ErrorCodes.Overloaded => "server is at capacity, retry shortly",
        ErrorCodes.DbUnavailable => "database unavailable",
        _ => "internal error",
    };
}
