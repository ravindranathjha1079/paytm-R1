namespace SeatRes.Api.Domain;

public sealed record ReserveRequest(string[]? Seats, string? IdempotencyKey, bool? Confirm, string? UserId);

public sealed record ConfirmRequest(string? IdempotencyKey);

public sealed record ReservationDto(
    Guid ReservationId, Guid ShowId, string UserId, string[] Seats, long AmountPaise, string Status,
    DateTime? ExpiresAt, DateTime? PayDeadline, DateTime ServerTime);

/// <summary>A payment that has pinned its seats and must now be charged and finalized.</summary>
public sealed record PinnedPayment(Guid AttemptId, string GatewayKey, long AmountPaise, string? Hint);
