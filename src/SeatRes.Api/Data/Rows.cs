namespace SeatRes.Api.Data;

// Property-based (not positional) so Dapper can materialise array and nullable timestamp columns.

public sealed record SeatRow
{
    public int SeatNo { get; init; }
    public string Label { get; init; } = "";
    public string Status { get; init; } = "";
    public Guid? ReservationId { get; init; }
    public string? HolderUserId { get; init; }
    public DateTime? HoldExpiresAt { get; init; }
    public DateTime? PayDeadline { get; init; }
}

public sealed record ReservationRow
{
    public Guid Id { get; init; }
    public Guid ShowId { get; init; }
    public string UserId { get; init; } = "";
    public int[] SeatNos { get; init; } = [];
    public string[] Labels { get; init; } = [];
    public long AmountPaise { get; init; }
    public string Status { get; init; } = "";
    public DateTime? HoldExpiresAt { get; init; }
    public DateTime? PayDeadline { get; init; }
}

public static class Sql
{
    public const string SeatColumns =
        "seat_no AS SeatNo, label AS Label, status AS Status, reservation_id AS ReservationId, " +
        "holder_user_id AS HolderUserId, hold_expires_at AS HoldExpiresAt, pay_deadline AS PayDeadline";

    public const string ReservationColumns =
        "id AS Id, show_id AS ShowId, user_id AS UserId, seat_nos AS SeatNos, labels AS Labels, " +
        "amount_paise AS AmountPaise, status AS Status, hold_expires_at AS HoldExpiresAt, pay_deadline AS PayDeadline";
}

public static class ReservationStatus
{
    public const string Held = "held";
    public const string PaymentPending = "payment_pending";
    public const string Confirmed = "confirmed";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
    public const string RefundPending = "refund_pending";
    public const string Refunded = "refunded";
}
