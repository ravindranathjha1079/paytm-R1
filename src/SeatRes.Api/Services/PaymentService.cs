using Microsoft.Extensions.Options;
using SeatRes.Api.Data;
using SeatRes.Api.Domain;
using SeatRes.Api.Observability;
using SeatRes.Api.Payments;

namespace SeatRes.Api.Services;

public sealed class PaymentService(
    PaymentTx paymentTx,
    IPaymentGateway gateway,
    OutcomeRecorder outcomes,
    IOptions<SeatResOptions> options,
    ILogger<PaymentService> log)
{
    public async Task<ApiResult> ConfirmAsync(Guid reservationId, string userId, ConfirmRequest? body, string? headerKey,
        string? hint, CancellationToken ct)
    {
        var (key, keyError) = IdempotencyKey.Resolve(headerKey, body?.IdempotencyKey);
        if (keyError is not null) return keyError;

        var pin = await paymentTx.PinAsync(reservationId, userId, key!, RequestHash.ForConfirm(reservationId), hint, ct);
        var result = pin.Pinned is null ? pin.Response! : await RunAsync(pin.Pinned);
        return outcomes.Record("confirm", result, userId, pin.ShowId, reservationId, null);
    }

    /// <summary>
    /// Charges a pinned payment and finalizes it. Deliberately ignores request cancellation: once money may be
    /// in motion the outcome must be recorded even if the client has gone away.
    /// </summary>
    public async Task<ApiResult> RunAsync(PinnedPayment pinned)
    {
        GatewayStatus outcome;
        try
        {
            outcome = await gateway.ChargeAsync(pinned.GatewayKey, pinned.AmountPaise, pinned.Hint, CancellationToken.None)
                .WaitAsync(TimeSpan.FromMilliseconds(options.Value.GatewayTimeoutMillis));
        }
        catch (TimeoutException)
        {
            outcome = GatewayStatus.Unknown;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "{event} {gateway_key}", "gateway.charge_failed", pinned.GatewayKey);
            outcome = GatewayStatus.Unknown;
        }
        SeatResMetrics.Payments.WithLabels(outcome.MetricLabel()).Inc();
        log.LogInformation("{event} {gateway_key} {outcome}", "gateway.charge", pinned.GatewayKey, outcome);

        var final = await paymentTx.FinalizeAsync(pinned.AttemptId, outcome, CancellationToken.None);
        if (final.RefundAttemptId is { } refundAttempt)
            await RefundAsync(refundAttempt);
        return final.Response;
    }

    public async Task<bool> RefundAsync(Guid attemptId)
    {
        var attempt = await paymentTx.GetAttemptAsync(attemptId, CancellationToken.None);
        if (attempt is null || attempt.Status != "refund_pending") return false;
        try
        {
            var ok = await gateway.RefundAsync(attempt.GatewayKey, CancellationToken.None)
                .WaitAsync(TimeSpan.FromMilliseconds(options.Value.GatewayTimeoutMillis));
            if (!ok)
            {
                log.LogWarning("{event} {gateway_key}", "gateway.refund_rejected", attempt.GatewayKey);
                return false;
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "{event} {gateway_key}", "gateway.refund_failed", attempt.GatewayKey);
            return false;
        }
        var reason = await paymentTx.MarkRefundedAsync(attemptId, CancellationToken.None);
        log.LogInformation("{event} {gateway_key} {reason}", "refund.issued", attempt.GatewayKey, reason);
        return true;
    }
}
