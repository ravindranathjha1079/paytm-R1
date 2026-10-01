namespace SeatRes.Api.Domain;

/// <summary>Stored values of seats.status.</summary>
public static class SeatStatus
{
    public const string Available = "available";
    public const string Held = "held";
    public const string PaymentPending = "payment_pending";
    public const string Confirmed = "confirmed";
}

/// <summary>
/// The seat state machine in one place. The SQL fragments and the C# predicates must agree;
/// the unit tests pin the C# side and the integration tests exercise the SQL side.
/// Nothing releases seats on a timer: an expired hold or payment pin simply becomes takeable.
/// </summary>
public static class SeatRules
{
    public const string TakeableSql =
        "(status = 'available' OR (status = 'held' AND hold_expires_at <= @now) " +
        "OR (status = 'payment_pending' AND pay_deadline <= @now))";

    public const string EffectiveStatusSql =
        "CASE WHEN status = 'confirmed' THEN 'confirmed' " +
        "WHEN status = 'held' AND hold_expires_at > @now THEN 'held' " +
        "WHEN status = 'payment_pending' AND pay_deadline > @now THEN 'held' " +
        "ELSE 'available' END";

    public static bool IsTakeable(string status, DateTime? holdExpiresAt, DateTime? payDeadline, DateTime now) =>
        status switch
        {
            SeatStatus.Available => true,
            SeatStatus.Held => holdExpiresAt <= now,
            SeatStatus.PaymentPending => payDeadline <= now,
            _ => false,
        };

    public static string Effective(string status, DateTime? holdExpiresAt, DateTime? payDeadline, DateTime now) =>
        status switch
        {
            SeatStatus.Confirmed => "confirmed",
            SeatStatus.Held when holdExpiresAt > now => "held",
            SeatStatus.PaymentPending when payDeadline > now => "held",
            _ => "available",
        };

    /// <summary>
    /// When a payment starts the seats are pinned until <c>greatest(hold_expires_at, now) + grace</c>,
    /// unless a pin is already running — retries never extend it.
    /// </summary>
    public static DateTime PayDeadline(string reservationStatus, DateTime? holdExpiresAt, DateTime? currentDeadline,
        DateTime now, TimeSpan grace)
    {
        if (reservationStatus == SeatStatus.PaymentPending && currentDeadline > now)
            return currentDeadline.Value;
        var baseline = holdExpiresAt is { } hold && hold > now ? hold : now;
        return baseline + grace;
    }
}
