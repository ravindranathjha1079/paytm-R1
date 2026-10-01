namespace SeatRes.Api.Domain;

public static class IdempotencyKey
{
    public const string Header = "Idempotency-Key";
    public const int MaxLength = 128;

    /// <summary>The key may come from the header or the body; if both are present they must agree.</summary>
    public static (string? Key, ApiResult? Error) Resolve(string? header, string? body)
    {
        header = string.IsNullOrWhiteSpace(header) ? null : header;
        body = string.IsNullOrWhiteSpace(body) ? null : body;

        if (header is not null && body is not null && header != body)
            return (null, Bad("Idempotency-Key header and idempotency_key body field differ"));

        var key = header ?? body;
        if (key is null)
            return (null, Bad("an idempotency key is required (Idempotency-Key header or idempotency_key)"));
        if (key.Length > MaxLength || key.Any(char.IsControl))
            return (null, Bad($"idempotency key must be at most {MaxLength} printable characters"));
        return (key, null);
    }

    private static ApiResult Bad(string message) => ApiResult.Fail(400, ErrorCodes.BadRequest, message);
}
