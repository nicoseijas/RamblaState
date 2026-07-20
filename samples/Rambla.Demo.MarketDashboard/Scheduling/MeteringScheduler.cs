using System.Diagnostics;
using Rambla.Demo.MarketDashboard.Diagnostics;
using Rambla.Scheduling;

namespace Rambla.Demo.MarketDashboard.Scheduling;

/// <summary>
/// Wraps any <see cref="IStateScheduler"/> to count scheduler posts and time each
/// flush at the exact boundary where the UI cost lands. The timing brackets the
/// real notification pass, so the percentile reservoir reflects what the UI
/// thread actually spent flushing.
/// </summary>
public sealed class MeteringScheduler : IStateScheduler
{
    private readonly IStateScheduler _inner;
    private readonly DemoMetrics _metrics;

    public MeteringScheduler(IStateScheduler inner, DemoMetrics metrics)
    {
        _inner = inner;
        _metrics = metrics;
    }

    public void Post(Action flush)
    {
        _metrics.OnSchedulerPost();
        _inner.Post(() =>
        {
            long start = Stopwatch.GetTimestamp();
            flush();
            _metrics.OnFlush(Stopwatch.GetTimestamp() - start);
        });
    }
}
