using System.Diagnostics;
using System.Threading;

namespace Rambla.Demo.MarketDashboard.Diagnostics;

/// <summary>
/// Measures end-to-end <em>producer → visible value</em> latency. The feed stamps
/// a monotonically increasing sequence number into a canary row and records the
/// produce time; on each rendered frame the UI reads the canary's current model
/// value and, for a sequence it hasn't seen, records how long that value took to
/// become visible. This is the latency coalescing trades for fewer notifications
/// — the number that decides the refresh-rate sweet spot.
/// </summary>
public sealed class LatencyProbe
{
    private const int Capacity = 1 << 16; // ring of recent produce timestamps

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private readonly long[] _stamps = new long[Capacity];
    private long _seq;
    private long _lastObserved;
    private PercentileReservoir _latencyMs = new();

    /// <summary>Called on the feed thread: stamps now and returns the new sequence value to publish.</summary>
    public decimal NextValue()
    {
        long seq = Interlocked.Increment(ref _seq);
        Volatile.Write(ref _stamps[seq & (Capacity - 1)], Stopwatch.GetTimestamp());
        return seq;
    }

    /// <summary>Called on the UI render thread with the canary's currently visible value.</summary>
    public void Observe(decimal visibleValue)
    {
        // Guard against torn decimal reads across threads and stale/duplicate frames.
        if (visibleValue <= 0m || visibleValue > Volatile.Read(ref _seq))
        {
            return;
        }

        long seq = (long)visibleValue;
        if (seq == _lastObserved)
        {
            return;
        }

        _lastObserved = seq;
        long produced = Volatile.Read(ref _stamps[seq & (Capacity - 1)]);
        if (produced == 0)
        {
            return;
        }

        double latencyMs = (Stopwatch.GetTimestamp() - produced) * TicksToMs;
        if (latencyMs >= 0)
        {
            _latencyMs.Add(latencyMs);
        }
    }

    public double P50Ms => _latencyMs.Percentile(0.50);

    public double P95Ms => _latencyMs.Percentile(0.95);

    public double P99Ms => _latencyMs.Percentile(0.99);

    public void Reset()
    {
        _latencyMs = new PercentileReservoir();
        _lastObserved = 0;
    }
}
