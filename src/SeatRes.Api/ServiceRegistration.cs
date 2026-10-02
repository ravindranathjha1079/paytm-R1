using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Npgsql;
using Prometheus;
using SeatRes.Api.Auth;
using SeatRes.Api.Background;
using SeatRes.Api.Concurrency;
using SeatRes.Api.Data;
using SeatRes.Api.Domain;
using SeatRes.Api.Endpoints;
using SeatRes.Api.Observability;
using SeatRes.Api.Payments;
using SeatRes.Api.Services;
using Serilog;
using Serilog.Events;

namespace SeatRes.Api;

public static class ServiceRegistration
{
    public const string WritePolicy = "writes";
    public const string ReadPolicy = "reads";

    public static IServiceCollection AddSeatRes(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<SeatResOptions>().Bind(config.GetSection(SeatResOptions.Section));
        services.AddOptions<AuthOptions>().Bind(config.GetSection(AuthOptions.Section));
        services.AddOptions<GatewayOptions>().Bind(config.GetSection(GatewayOptions.Section));
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton(sp =>
        {
            var cs = sp.GetRequiredService<IConfiguration>().GetConnectionString("Db")
                     ?? throw new InvalidOperationException("ConnectionStrings:Db is required");
            var builder = new NpgsqlDataSourceBuilder(cs) { Name = "app" };
            return builder.Build();
        });
        services.AddSingleton<Db>();
        services.AddSingleton<Migrator>();
        services.AddSingleton<MigrationState>();
        services.AddHostedService<MigrationHostedService>();
        services.AddSingleton<ShowStore>();
        services.AddSingleton<ShowCatalog>();
        services.AddSingleton<ReserveTx>();
        services.AddSingleton<ReservationStore>();
        services.AddSingleton<OutcomeRecorder>();
        services.AddSingleton<ReservationService>();
        services.AddSingleton<PaymentTx>();
        services.AddSingleton<CancelTx>();
        services.AddSingleton<PaymentService>();
        services.AddSingleton<GatewayDataSource>();
        services.AddSingleton<IPaymentGateway, SimulatedGateway>();
        services.AddSingleton<TakenSeatCache>();
        services.AddSingleton<SeatGate>();
        services.AddSingleton<PaymentRecovery>();
        services.AddSingleton<Reconciler>();
        services.AddSingleton<SeatGauge>();
        services.AddHostedService<PeriodicWorker>();
        services.AddSeatResAuth();

        // Admission control for writes: bounded in-flight + bounded queue; beyond that a fast 429, never a 5xx.
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<SeatResOptions>>((o, seatres) =>
        {
            o.AddConcurrencyLimiter(WritePolicy, c =>
            {
                c.PermitLimit = seatres.Value.AdmissionPermits;
                c.QueueLimit = seatres.Value.AdmissionQueue;
                c.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });
            // Reads (show state polling) get their own budget so they can never starve writes of DB connections.
            o.AddConcurrencyLimiter(ReadPolicy, c =>
            {
                c.PermitLimit = 128;
                c.QueueLimit = 5_000;
                c.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });
            o.OnRejected = async (ctx, _) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "1";
                SeatResMetrics.Declined.WithLabels(ErrorCodes.Overloaded).Inc();
                await ApiResult.Fail(429, ErrorCodes.Overloaded, "server is at capacity, retry shortly").WriteAsync(ctx.HttpContext);
            };
        });

        services.Configure<JsonOptions>(o => Json.Configure(o.SerializerOptions));
        services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info.Title = "SeatRes — seat reservation at scale";
            doc.Info.Description = "Atomic seat holds, idempotent reserves and payments. Money is integer paise. See README.md.";
            return Task.CompletedTask;
        }));
        services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

        // Keep /metrics to what matters: our metrics, HTTP, runtime meters and Npgsql pool meters.
        Metrics.SuppressDefaultMetrics(new SuppressDefaultMetricOptions { SuppressEventCounters = true });
        SeatResMetrics.Initialise();
        return services;
    }

    public static WebApplication UseSeatResPipeline(this WebApplication app)
    {
        app.Services.GetRequiredService<SeatGauge>().Register();
        app.UseMiddleware<RequestIdMiddleware>();
        // Outside the error handler, so a request the handler turns into a 500 is counted as a 500.
        app.UseHttpMetrics(o => o.ReduceStatusCodeCardinality());
        app.UseSerilogRequestLogging(o =>
        {
            o.MessageTemplate = "http {RequestMethod} {RequestPath} {StatusCode} {Elapsed:0.0}ms";
            o.GetLevel = (ctx, _, ex) => ex is not null || ctx.Response.StatusCode >= 500
                ? LogEventLevel.Error
                : LogEventLevel.Information;
        });
        app.UseMiddleware<ErrorHandlingMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        return app;
    }

    public static WebApplication MapSeatResEndpoints(this WebApplication app)
    {
        app.MapHealthEndpoints();
        app.MapAuthEndpoints();
        app.MapShowEndpoints();
        app.MapReservationEndpoints();
        app.MapMetrics("/metrics");
        app.MapOpenApi();
        if (app.Environment.IsEnvironment("Testing"))
            app.MapGet("/__test/boom", (Func<IResult>)(() => throw new InvalidOperationException("deliberate test failure")));
        return app;
    }
}
