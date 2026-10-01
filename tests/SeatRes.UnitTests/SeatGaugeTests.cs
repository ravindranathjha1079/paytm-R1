using SeatRes.Api.Background;

namespace SeatRes.UnitTests;

public class SeatGaugeTests
{
    [Fact]
    public void A_gauge_that_never_refreshed_is_stale() =>
        Assert.True(SeatGauge.IsStale(lastRefreshMs: SeatGauge.Never, nowMs: Environment.TickCount64));

    [Fact]
    public void A_gauge_refreshed_within_the_last_second_is_fresh() =>
        Assert.False(SeatGauge.IsStale(lastRefreshMs: 10_000, nowMs: 10_500));

    [Fact]
    public void A_gauge_refreshed_a_second_ago_is_stale() =>
        Assert.True(SeatGauge.IsStale(lastRefreshMs: 10_000, nowMs: 11_000));
}
