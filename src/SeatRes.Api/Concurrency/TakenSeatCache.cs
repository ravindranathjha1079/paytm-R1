using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace SeatRes.Api.Concurrency;

/// <param name="Holder">Who holds the seat, when known. Only the holder can own a winning idempotency key for it.</param>
public readonly record struct TakenSeatEntry(int SeatNo, string? Holder);

/// <summary>
/// Remembers, for at most TakenCacheMillis (and never past the seat's own expiry), that a seat was just taken,
/// so a stampede on a hot seat is declined from memory. It can only ever cause a decline — never a sale —
/// so a stale entry costs at worst a "seat taken" during the instant the seat was changing hands.
/// </summary>
public sealed class TakenSeatCache(TimeProvider time, IOptions<SeatResOptions> options)
{
    private readonly record struct Entry(long Until, string? Holder);
    private readonly ConcurrentDictionary<(Guid Show, int SeatNo), Entry> _taken = new();

    public void MarkTaken(Guid showId, IEnumerable<int> seatNos, DateTime? notAfter, string? holder = null)
    {
        var max = time.GetUtcNow().UtcDateTime.AddMilliseconds(options.Value.TakenCacheMillis);
        var until = (notAfter is { } n && n < max ? n : max).Ticks;
        foreach (var no in seatNos) _taken[(showId, no)] = new Entry(until, holder);
    }

    public void Invalidate(Guid showId, IEnumerable<int> seatNos)
    {
        foreach (var no in seatNos) _taken.TryRemove((showId, no), out _);
    }

    public int[] FindTaken(Guid showId, int[] seatNos) => FindTakenWithHolders(showId, seatNos).Select(e => e.SeatNo).ToArray();

    public TakenSeatEntry[] FindTakenWithHolders(Guid showId, int[] seatNos)
    {
        var now = time.GetUtcNow().UtcDateTime.Ticks;
        List<TakenSeatEntry>? taken = null;
        foreach (var no in seatNos)
        {
            if (!_taken.TryGetValue((showId, no), out var entry)) continue;
            if (entry.Until > now) (taken ??= []).Add(new TakenSeatEntry(no, entry.Holder));
            else _taken.TryRemove(new KeyValuePair<(Guid, int), Entry>((showId, no), entry));
        }
        return taken?.ToArray() ?? [];
    }
}
