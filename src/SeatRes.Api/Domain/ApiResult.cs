using System.Text.Json;
using System.Text.Json.Nodes;

namespace SeatRes.Api.Domain;

/// <summary>
/// Every endpoint outcome: status, JSON body, and the domain reason (for metrics / logs).
/// The body is a JsonObject so it can be stored verbatim for idempotent replay.
/// </summary>
public sealed record ApiResult(int StatusCode, JsonObject? Body, string? Reason = null, bool Replayed = false) : IResult
{
    public static ApiResult Ok<T>(T dto, int status = 200) => new(status, Json.ToNode(dto));

    public static ApiResult Fail(int status, string code, string message, IDictionary<string, object?>? extra = null)
    {
        var body = new JsonObject { ["error"] = code, ["message"] = message };
        if (extra is not null)
            foreach (var (k, v) in extra)
                body[k] = JsonSerializer.SerializeToNode(v, Json.Options);
        return new ApiResult(status, body, code);
    }

    public bool IsError => Body?.ContainsKey("error") == true;

    public async Task WriteAsync(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCode;
        if (Replayed) ctx.Response.Headers["Idempotent-Replayed"] = "true";
        if (Body is null) return;
        var body = (JsonObject)Body.DeepClone();
        if (IsError) body["request_id"] = ctx.TraceIdentifier;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(body.ToJsonString());
    }

    public Task ExecuteAsync(HttpContext httpContext) => WriteAsync(httpContext);
}
