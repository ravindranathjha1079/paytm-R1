using Microsoft.Extensions.Options;
using SeatRes.Api.Observability;

namespace SeatRes.Api.Concurrency;

/// <summary>
/// Striped in-process locks so that, per seat, only one request at a time goes to the database; the rest wait
/// in memory instead of holding a pooled connection blocked on a row lock. Stripes are taken in ascending
/// index order (deduplicated), so multi-seat requests cannot deadlock. All stripes are acquired before the
/// DB transaction opens. A wait that times out falls through ungated — the database still decides.
/// </summary>
public sealed class SeatGate(IOptions<SeatResOptions> options)
{
    private const int StripeCount = 4096;
    private readonly SemaphoreSlim[] _stripes = Enumerable.Range(0, StripeCount).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public static int[] StripesFor(Guid showId, int[] seatNos) =>
        seatNos.Select(no => (int)((uint)HashCode.Combine(showId, no) % StripeCount)).Distinct().Order().ToArray();

    public async Task<Lease> AcquireAsync(Guid showId, int[] seatNos, CancellationToken ct)
    {
        var wanted = StripesFor(showId, seatNos);
        var held = new List<int>(wanted.Length);
        var timeout = TimeSpan.FromMilliseconds(options.Value.GateTimeoutMillis);
        try
        {
            foreach (var stripe in wanted)
            {
                if (!await _stripes[stripe].WaitAsync(timeout, ct))
                {
                    Release(held);
                    SeatResMetrics.GateTimeouts.Inc();
                    return new Lease(this, [], gated: false);
                }
                held.Add(stripe);
            }
        }
        catch
        {
            Release(held);
            throw;
        }
        return new Lease(this, held.ToArray(), gated: true);
    }

    private void Release(IEnumerable<int> stripes)
    {
        foreach (var s in stripes) _stripes[s].Release();
    }

    public sealed class Lease : IDisposable
    {
        public static readonly Lease None = new(null, [], gated: false);
        private readonly SeatGate? _gate;
        private int[] _stripes;

        internal Lease(SeatGate? gate, int[] stripes, bool gated)
        {
            _gate = gate;
            _stripes = stripes;
            Gated = gated;
        }

        public bool Gated { get; }

        public void Dispose()
        {
            var stripes = Interlocked.Exchange(ref _stripes, []);
            _gate?.Release(stripes);
        }
    }
}
