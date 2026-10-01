using System.Text.RegularExpressions;
using Serilog.Context;

namespace SeatRes.Api.Observability;

public sealed partial class RequestIdMiddleware(RequestDelegate next)
{
    public const string Header = "X-Request-Id";

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex Valid();

    public async Task InvokeAsync(HttpContext ctx)
    {
        var incoming = ctx.Request.Headers[Header].ToString();
        var id = Valid().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");
        ctx.TraceIdentifier = id;
        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers[Header] = id;
            return Task.CompletedTask;
        });
        using (LogContext.PushProperty("request_id", id))
            await next(ctx);
    }
}
