using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SeatRes.Api;
using SeatRes.Api.Concurrency;

namespace SeatRes.UnitTests;

public class TakenSeatCacheTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero));
    private readonly Guid _show = Guid.NewGuid();
    private TakenSeatCache NewCache(int ttlMs = 1000) =>
        new(_clock, Options.Create(new SeatResOptions { TakenCacheMillis = ttlMs }));

    [Fact]
    public void Unknown_seats_are_not_taken() => Assert.Empty(NewCache().FindTaken(_show, [1, 2]));

    [Fact]
    public void Marked_seats_are_taken_until_the_ttl()
    {
        var cache = NewCache();
        cache.MarkTaken(_show, [2], notAfter: null);
        Assert.Equal([2], cache.FindTaken(_show, [1, 2]));
        _clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal([2], cache.FindTaken(_show, [2]));
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Empty(cache.FindTaken(_show, [2]));
    }

    [Fact]
    public void An_entry_never_outlives_the_seats_own_expiry()
    {
        var cache = NewCache();
        cache.MarkTaken(_show, [1], notAfter: _clock.GetUtcNow().UtcDateTime.AddMilliseconds(200));
        _clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Empty(cache.FindTaken(_show, [1]));
    }

    [Fact]
    public void Invalidate_forgets_seats_immediately()
    {
        var cache = NewCache();
        cache.MarkTaken(_show, [1, 2], null);
        cache.Invalidate(_show, [1]);
        Assert.Equal([2], cache.FindTaken(_show, [1, 2]));
    }

    [Fact]
    public void Shows_are_independent()
    {
        var cache = NewCache();
        cache.MarkTaken(_show, [1], null);
        Assert.Empty(cache.FindTaken(Guid.NewGuid(), [1]));
    }
}

public class SeatGateTests
{
    private static SeatGate NewGate(int timeoutMs = 2000) =>
        new(Options.Create(new SeatResOptions { GateTimeoutMillis = timeoutMs }));

    [Fact]
    public void Stripes_are_sorted_and_unique()
    {
        var stripes = SeatGate.StripesFor(Guid.NewGuid(), [9, 3, 3, 7, 1]);
        Assert.Equal(stripes.Order().Distinct(), stripes);
    }

    [Fact]
    public async Task A_seat_gate_admits_one_holder_at_a_time()
    {
        var gate = NewGate();
        var show = Guid.NewGuid();
        var first = await gate.AcquireAsync(show, [5], CancellationToken.None);
        Assert.True(first.Gated);
        var second = gate.AcquireAsync(show, [5], CancellationToken.None);
        await Task.Delay(50);
        Assert.False(second.IsCompleted);
        first.Dispose();
        using var lease = await second;
        Assert.True(lease.Gated);
    }

    [Fact]
    public async Task Overlapping_multi_seat_leases_do_not_deadlock()
    {
        var gate = NewGate();
        var show = Guid.NewGuid();
        var tasks = Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            int[] seats = i % 2 == 0 ? [1, 2, 3] : [3, 2, 1];
            using var lease = await gate.AcquireAsync(show, seats, CancellationToken.None);
            Assert.True(lease.Gated);
            await Task.Yield();
        }));
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_timed_out_wait_falls_through_ungated()
    {
        var gate = NewGate(timeoutMs: 50);
        var show = Guid.NewGuid();
        using var held = await gate.AcquireAsync(show, [1], CancellationToken.None);
        using var lease = await gate.AcquireAsync(show, [1], CancellationToken.None);
        Assert.False(lease.Gated);
    }
}
