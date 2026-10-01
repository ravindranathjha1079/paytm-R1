using SeatRes.Api.Domain;
using SeatRes.Api.Observability;

namespace SeatRes.Api.Services;

/// <summary>One structured log line and the decline metrics for every domain decision.</summary>
public sealed class OutcomeRecorder(ILogger<OutcomeRecorder> log)
{
    private static readonly HashSet<string> Declines = [.. ErrorCodes.DomainDeclines];

    public ApiResult Record(string action, ApiResult result, string userId, Guid? showId, Guid? reservationId,
        IReadOnlyCollection<string>? seats, string? layer = null)
    {
        var reason = result.Replayed ? ErrorCodes.IdempotentReplay : result.Reason;
        if ((result.Replayed || result.StatusCode >= 400) && reason is not null && Declines.Contains(reason))
            SeatResMetrics.Declined.WithLabels(reason).Inc();
        if (layer is not null)
            SeatResMetrics.FastPathDeclines.WithLabels(layer).Inc();

        log.LogInformation(
            "{event} {status_code} {reason} {user_id} {show_id} {reservation_id} {seats} {layer}",
            action + ".outcome", result.StatusCode, reason, userId, showId, reservationId, seats, layer);
        return result;
    }
}
