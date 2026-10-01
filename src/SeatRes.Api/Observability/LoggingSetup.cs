using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace SeatRes.Api.Observability;

public static class LoggingSetup
{
    public static void Configure(IConfiguration config, LoggerConfiguration lc) => lc
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
        .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
        .ReadFrom.Configuration(config)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("service", "seatres-api")
        // Async + non-blocking: under a burst we drop log lines rather than stall request threads.
        .WriteTo.Async(a => a.Console(new RenderedCompactJsonFormatter()), bufferSize: 50_000, blockWhenFull: false);
}
