namespace SeatRes.Api.Payments;

public enum GatewayStatus { Succeeded, Declined, Unknown, NotFound }

/// <summary>
/// A payment provider. Every call is idempotent per <c>key</c>: charging the same key twice
/// returns the first outcome and never moves money twice.
/// </summary>
public interface IPaymentGateway
{
    Task<GatewayStatus> ChargeAsync(string key, long amountPaise, string? hint, CancellationToken ct);
    Task<GatewayStatus> QueryAsync(string key, CancellationToken ct);
    Task<bool> RefundAsync(string key, CancellationToken ct);
}

public static class GatewayStatusExtensions
{
    public static string MetricLabel(this GatewayStatus s) => s switch
    {
        GatewayStatus.Succeeded => "succeeded",
        GatewayStatus.Unknown => "unknown",
        _ => "declined",
    };
}
