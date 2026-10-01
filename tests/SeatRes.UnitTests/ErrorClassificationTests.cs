using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Npgsql;
using SeatRes.Api.Domain;
using SeatRes.Api.Observability;

namespace SeatRes.UnitTests;

public class ErrorClassificationTests
{
    [Theory]
    [InlineData("40P01")] // deadlock_detected
    [InlineData("40001")] // serialization_failure
    [InlineData("55P03")] // lock_not_available (lock_timeout)
    [InlineData("57014")] // query_canceled (statement_timeout)
    public void Transient_postgres_errors_become_429_overloaded(string sqlState)
    {
        var ex = new PostgresException("boom", "ERROR", "ERROR", sqlState);
        Assert.Equal((429, ErrorCodes.Overloaded), ErrorHandlingMiddleware.Classify(ex));
    }

    [Fact]
    public void Pool_exhaustion_becomes_429_overloaded()
    {
        var ex = new NpgsqlException(
            "The connection pool has been exhausted, either raise 'Max Pool Size' (currently 30) or 'Timeout' (currently 15 seconds) in your connection string.",
            new TimeoutException());
        Assert.Equal((429, ErrorCodes.Overloaded), ErrorHandlingMiddleware.Classify(ex));
    }

    [Fact]
    public void Unreachable_database_becomes_503_db_unavailable()
    {
        var ex = new NpgsqlException("Failed to connect to 127.0.0.1:5432", new System.Net.Sockets.SocketException());
        Assert.Equal((503, ErrorCodes.DbUnavailable), ErrorHandlingMiddleware.Classify(ex));
    }

    [Fact]
    public void Malformed_requests_become_400()
    {
        Assert.Equal((400, ErrorCodes.BadRequest), ErrorHandlingMiddleware.Classify(new BadHttpRequestException("bad")));
        Assert.Equal((400, ErrorCodes.BadRequest), ErrorHandlingMiddleware.Classify(new JsonException("bad")));
    }

    [Fact]
    public void Non_transient_postgres_error_is_500()
    {
        var ex = new PostgresException("constraint", "ERROR", "ERROR", "23505");
        Assert.Equal((500, ErrorCodes.Internal), ErrorHandlingMiddleware.Classify(ex));
    }

    [Fact]
    public void Unknown_exceptions_are_500_internal()
    {
        Assert.Equal((500, ErrorCodes.Internal), ErrorHandlingMiddleware.Classify(new InvalidOperationException()));
    }
}
