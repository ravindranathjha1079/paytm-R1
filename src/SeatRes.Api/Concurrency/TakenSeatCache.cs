using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace SeatRes.Api.Concurrency;

/// <summary>
/// Remembers, for at most TakenCacheMillis (and never past the seat's own expiry), that a seat was just taken,
/// so a stampede on a hot seat is declined from memory. It can only ever cause a decline — never a sale —
/// so a stale entry costs at worst a "seat taken" during the instant the seat was changing hands.
/// </summary>
public sealed class TakenSeatCache(TimeProvider time, IOptions<SeatResOptions> options)
{
    private readonly ConcurrentDictionary<(Guid Show, int SeatNo), long> _takenUntil = new();

    public void MarkTaken(Guid showId, IEnumerable<int> seatNos, DateTime? notAfter)
    {
        var max = time.GetUtcNow().UtcDateTime.AddMilliseconds(options.Value.TakenCacheMillis);
        var until = (notAfter is { } n && n < max ? n : max).Ticks;
        foreach (var no in seatNos) _takenUntil[(showId, no)] = until;
    }

    public void Invalidate(Guid showId, IEnumerable<int> seatNos)
    {
        foreach (var no in seatNos) _takenUntil.TryRemove((showId, no), out _);
    }

    public int[] FindTaken(Guid showId, int[] seatNos)
    {
        var now = time.GetUtcNow().UtcDateTime.Ticks;
        List<int>? taken = null;
        foreach (var no in seatNos)
        {
            if (!_takenUntil.TryGetValue((showId, no), out var until)) continue;
            if (until > now) (taken ??= []).Add(no);
            else _takenUntil.TryRemove(new KeyValuePair<(Guid, int), long>((showId, no), until));
        }
        return taken?.ToArray() ?? [];
    }
}
