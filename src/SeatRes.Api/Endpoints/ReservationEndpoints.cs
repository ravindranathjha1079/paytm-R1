using SeatRes.Api.Auth;
using SeatRes.Api.Data;
using SeatRes.Api.Domain;
using SeatRes.Api.Services;

namespace SeatRes.Api.Endpoints;

public static class ReservationEndpoints
{
    public const string GatewayHintHeader = "X-Sim-Gateway";

    public static void MapReservationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/shows/{id:guid}/reserve", (Guid id, ReserveRequest body, HttpContext ctx, ReservationService svc, CancellationToken ct) =>
                svc.ReserveAsync(id, ctx.User.UserId(), body, ctx.Request.Headers[IdempotencyKey.Header].FirstOrDefault(),
                    ctx.Request.Headers[GatewayHintHeader].FirstOrDefault(), ct))
            .RequireAuthorization(AuthSetup.UserPolicy);

        app.MapPost("/reservations/{id:guid}/confirm", (Guid id, ConfirmRequest? body, HttpContext ctx, PaymentService svc, CancellationToken ct) =>
                svc.ConfirmAsync(id, ctx.User.UserId(), body, ctx.Request.Headers[IdempotencyKey.Header].FirstOrDefault(),
                    ctx.Request.Headers[GatewayHintHeader].FirstOrDefault(), ct))
            .RequireAuthorization(AuthSetup.UserPolicy);

        app.MapPost("/reservations/{id:guid}/cancel", (Guid id, HttpContext ctx, PaymentService svc, CancellationToken ct) =>
                svc.CancelAsync(id, ctx.User.UserId(), ct))
            .RequireAuthorization(AuthSetup.UserPolicy);

        app.MapGet("/reservations/{id:guid}", async (Guid id, HttpContext ctx, ReservationStore store, TimeProvider time, CancellationToken ct) =>
            {
                var r = await store.GetAsync(id, ct);
                if (r is null || r.UserId != ctx.User.UserId())
                    return ApiResult.Fail(404, ErrorCodes.NotFound, "reservation not found");
                return ApiResult.Ok(ReservationStore.ToDto(r, time.GetUtcNow().UtcDateTime));
            })
            .RequireAuthorization(AuthSetup.UserPolicy);
    }
}
