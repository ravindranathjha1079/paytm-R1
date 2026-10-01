using Microsoft.AspNetCore.Http.Json;
using Prometheus;
using SeatRes.Api.Domain;
using SeatRes.Api.Endpoints;
using SeatRes.Api.Observability;
using Serilog;
using Serilog.Events;

namespace SeatRes.Api;

public static class ServiceRegistration
{
    public static IServiceCollection AddSeatRes(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<SeatResOptions>().Bind(config.GetSection(SeatResOptions.Section));
        services.AddOptions<AuthOptions>().Bind(config.GetSection(AuthOptions.Section));
        services.AddOptions<GatewayOptions>().Bind(config.GetSection(GatewayOptions.Section));
        services.AddSingleton(TimeProvider.System);

        services.Configure<JsonOptions>(o => Json.Configure(o.SerializerOptions));
        services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

        SeatResMetrics.Initialise();
        return services;
    }

    public static WebApplication UseSeatResPipeline(this WebApplication app)
    {
        app.UseMiddleware<RequestIdMiddleware>();
        app.UseSerilogRequestLogging(o =>
        {
            o.MessageTemplate = "http {RequestMethod} {RequestPath} {StatusCode} {Elapsed:0.0}ms";
            o.GetLevel = (ctx, _, ex) => ex is not null || ctx.Response.StatusCode >= 500
                ? LogEventLevel.Error
                : LogEventLevel.Information;
        });
        app.UseMiddleware<ErrorHandlingMiddleware>();
        app.UseHttpMetrics(o => o.ReduceStatusCodeCardinality());
        return app;
    }

    public static WebApplication MapSeatResEndpoints(this WebApplication app)
    {
        app.MapHealthEndpoints();
        app.MapMetrics("/metrics");
        return app;
    }
}
