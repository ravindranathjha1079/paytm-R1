using Dapper;
using SeatRes.Api.Data;
using SeatRes.Api.Payments;
using SeatRes.Api.Services;

namespace SeatRes.Api.Background;

/// <summary>
/// Settles payments whose outcome was lost (gateway timeout, crash between charge and finalize) by asking
/// the gateway what happened, and retries refunds the gateway did not accept. Finalize is idempotent, so
/// racing a live request is harmless.
/// </summary>
public sealed class PaymentRecovery(
    Db db, PaymentTx paymentTx, PaymentService payments, IPaymentGateway gateway, TimeProvider time,
    ILogger<PaymentRecovery> log)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(2);

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var stale = (await c.QueryAsync<AttemptRow>(
            $"""
            SELECT {PaymentTx.AttemptColumns} FROM payment_attempts
            WHERE status IN ('pending', 'unknown') AND updated_at < @cutoff
            ORDER BY updated_at LIMIT 100
            """, new { cutoff = now - StaleAfter })).ToList();
        var refunds = (await c.QueryAsync<Guid>(
            "SELECT id FROM payment_attempts WHERE status = 'refund_pending' ORDER BY updated_at LIMIT 100")).ToList();

        var settled = 0;
        foreach (var attempt in stale)
        {
            var outcome = await gateway.QueryAsync(attempt.GatewayKey, ct);
            if (outcome == GatewayStatus.NotFound && attempt.CreatedAt > now - GiveUpAfter)
                continue; // the charge may still be in flight at the provider
            var final = await paymentTx.FinalizeAsync(attempt.Id,
                outcome == GatewayStatus.NotFound ? GatewayStatus.Declined : outcome, ct);
            log.LogInformation("{event} {gateway_key} {outcome} {status_code}", "recovery.finalized",
                attempt.GatewayKey, outcome, final.Response.StatusCode);
            if (final.RefundAttemptId is { } refund) await payments.RefundAsync(refund);
            settled++;
        }

        foreach (var attemptId in refunds)
            if (await payments.RefundAsync(attemptId)) settled++;
        return settled;
    }
}
