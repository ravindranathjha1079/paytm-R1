using Microsoft.AspNetCore.Http;
using SeatRes.Api;
using Serilog.Events;

namespace SeatRes.UnitTests;

public class RequestLogLevelTests
{
    [Theory]
    [InlineData("/shows/1/reserve", 409)]
    [InlineData("/reservations/1/confirm", 200)]
    [InlineData("/health/ready", 200)]
    [InlineData("/metrics", 200)]
    public void Routes_with_their_own_outcome_logs_or_constant_polling_are_quiet(string path, int status) =>
        Assert.Equal(LogEventLevel.Verbose, ServiceRegistration.RequestLogLevel(new PathString(path), status, null));

    [Fact]
    public void Server_errors_are_always_logged() =>
        Assert.Equal(LogEventLevel.Error, ServiceRegistration.RequestLogLevel(new PathString("/shows/1/reserve"), 503, null));

    [Fact]
    public void Exceptions_are_always_logged() =>
        Assert.Equal(LogEventLevel.Error, ServiceRegistration.RequestLogLevel(new PathString("/health/live"), 200, new Exception()));

    [Fact]
    public void Other_routes_keep_an_access_line() =>
        Assert.Equal(LogEventLevel.Information, ServiceRegistration.RequestLogLevel(new PathString("/auth/admin-token"), 200, null));
}
