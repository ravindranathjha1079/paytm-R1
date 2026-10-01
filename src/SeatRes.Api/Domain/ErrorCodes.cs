namespace SeatRes.Api.Domain;

public static class ErrorCodes
{
    // Domain declines (counted in seatres_reservations_declined_total).
    public const string SeatTaken = "seat_taken";
    public const string PerUserLimit = "per_user_limit";
    public const string IdempotentReplay = "idempotent_replay";
    public const string IdempotencyMismatch = "idempotency_mismatch";
    public const string HoldLost = "hold_lost";
    public const string PaymentDeclined = "payment_declined";
    public const string PaymentInProgress = "payment_in_progress";
    public const string ReservationClosed = "reservation_closed";
    public const string Overloaded = "overloaded";

    // Request / platform errors.
    public const string BadRequest = "bad_request";
    public const string UnknownSeats = "unknown_seats";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string DbUnavailable = "db_unavailable";
    public const string Internal = "internal";

    public static readonly string[] DomainDeclines =
    [
        SeatTaken, PerUserLimit, IdempotentReplay, IdempotencyMismatch, HoldLost,
        PaymentDeclined, PaymentInProgress, ReservationClosed, Overloaded,
    ];
}
