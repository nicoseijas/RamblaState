using System.Collections.Concurrent;
using System.Windows.Threading;
using Rambla.Scheduling;

namespace Rambla.Demo.MarketDashboard.Scheduling;

/// <summary>
/// The coalescing scheduler: a UI-thread <see cref="DispatcherTimer"/> ticks at a
/// fixed refresh rate (e.g. 60 Hz) and drains every pending flush posted since
/// the last tick. Because each <see cref="RamblaState"/> only posts one flush at
/// a time, all mutations that land between ticks fold into that single flush —
/// thousands of writes per second collapse into ~60 notification passes.
/// </summary>
public sealed class ThrottledDispatcherScheduler : IStateScheduler, IDisposable
{
    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly DispatcherTimer _timer;

    public ThrottledDispatcherScheduler(Dispatcher dispatcher, int refreshRateHz = 60)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(refreshRateHz);

        TimeSpan interval = TimeSpan.FromMilliseconds(1000.0 / refreshRateHz);
        _timer = new DispatcherTimer(interval, DispatcherPriority.Background, OnTick, dispatcher);
        _timer.Start();
    }

    // Called from the feed thread; a queue enqueue is thread-safe.
    public void Post(Action flush) => _pending.Enqueue(flush);

    private void OnTick(object? sender, EventArgs e)
    {
        // Snapshot the current backlog so flushes re-posted during draining wait
        // for the next tick instead of extending this one unbounded.
        int budget = _pending.Count;
        while (budget-- > 0 && _pending.TryDequeue(out Action? flush))
        {
            flush();
        }
    }

    public void Dispose() => _timer.Stop();
}
