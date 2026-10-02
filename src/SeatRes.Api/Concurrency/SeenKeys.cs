using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace SeatRes.Api.Concurrency;

/// <summary>
/// Fingerprints of every (user, idempotency key) this service has accepted for a reserve: added right after
/// the deciding transaction commits, and reloaded for the last <see cref="Window"/> at startup, before the
/// service reports ready.
///
/// It exists so a stampede can be declined from memory *exactly*. A key that was never accepted cannot be a
/// retry of anything, so if the taken-seat cache says "taken", answering 409 without reading the database is
/// precisely what the database would have answered. A key that might have been seen always goes to the
/// database (replay or mismatch). A fingerprint collision only costs an extra read. One process, one set: with
/// several replicas the fast path would need a shared set (or would be switched off).
/// </summary>
public sealed class SeenKeys
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<long, byte> _seen = new();

    public int Count => _seen.Count;

    public static long Fingerprint(string user, string key)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(user, "\n", key)), hash);
        return BitConverter.ToInt64(hash);
    }

    public void Add(string user, string key) => _seen.TryAdd(Fingerprint(user, key), 0);

    public bool MaybeSeen(string user, string key) => _seen.ContainsKey(Fingerprint(user, key));

    public async Task<int> LoadAsync(NpgsqlDataSource ds, DateTime since, CancellationToken ct)
    {
        await using var c = await ds.OpenConnectionAsync(ct);
        var rows = await c.QueryUnbufferedAsync<(string User, string Key)>(
            "SELECT user_id, key FROM idempotency_keys WHERE scope = 'reserve' AND created_at > @since", new { since })
            .ToListAsync(ct);
        foreach (var (user, key) in rows) Add(user, key);
        return rows.Count;
    }
}
