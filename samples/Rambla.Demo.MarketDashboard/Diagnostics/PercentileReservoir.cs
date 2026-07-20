namespace Rambla.Demo.MarketDashboard.Diagnostics;

/// <summary>
/// A fixed-capacity ring of recent samples with on-demand percentiles. Small,
/// lock-guarded, and good enough to characterize flush duration in the demo
/// without pulling in a histogram library. Written from whatever thread runs the
/// flush; read from the 1 Hz stats timer — hence the lock.
/// </summary>
public sealed class PercentileReservoir
{
    private readonly object _gate = new();
    private readonly double[] _samples;
    private int _count;
    private int _next;

    public PercentileReservoir(int capacity = 4096) => _samples = new double[capacity];

    public void Add(double value)
    {
        lock (_gate)
        {
            _samples[_next] = value;
            _next = (_next + 1) % _samples.Length;
            if (_count < _samples.Length)
            {
                _count++;
            }
        }
    }

    /// <param name="percentile">0..1 (e.g. 0.95 for p95).</param>
    public double Percentile(double percentile)
    {
        double[] snapshot;
        lock (_gate)
        {
            if (_count == 0)
            {
                return 0d;
            }

            snapshot = new double[_count];
            Array.Copy(_samples, snapshot, _count);
        }

        Array.Sort(snapshot);
        int index = (int)Math.Ceiling(percentile * snapshot.Length) - 1;
        index = Math.Clamp(index, 0, snapshot.Length - 1);
        return snapshot[index];
    }
}
