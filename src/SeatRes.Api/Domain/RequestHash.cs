using System.Security.Cryptography;
using System.Text;

namespace SeatRes.Api.Domain;

/// <summary>Fingerprint of a request body, stored with its idempotency key to detect key reuse with a different body.</summary>
public static class RequestHash
{
    public static byte[] ForReserve(Guid showId, IEnumerable<string> labels, bool confirm) =>
        Hash($"reserve|{showId:N}|{string.Join(',', labels.Order(StringComparer.Ordinal))}|{confirm}");

    public static byte[] ForConfirm(Guid reservationId) => Hash($"confirm|{reservationId:N}");

    private static byte[] Hash(string canonical) => SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
}
