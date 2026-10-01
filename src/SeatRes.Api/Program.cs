using SeatRes.Api;
using SeatRes.Api.Observability;
using Serilog;

if (args.Length > 0 && args[0] == "healthcheck")
    return await HealthProbe.RunAsync();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, lc) => LoggingSetup.Configure(ctx.Configuration, lc));
builder.Services.AddSeatRes(builder.Configuration);

var app = builder.Build();
app.UseSeatResPipeline();
app.MapSeatResEndpoints();
await app.RunAsync();
return 0;

public partial class Program;
