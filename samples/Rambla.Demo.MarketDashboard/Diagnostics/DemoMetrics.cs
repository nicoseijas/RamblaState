using System.Diagnostics;
using System.Threading;

namespace Rambla.Demo.MarketDashboard.Diagnostics;

/// <summary>
/// Thread-safe counters shared by the feed, the scheduler and the row observers.
/// The stats timer calls <see cref="Sample"/> once per second to turn cumulative
/// counters into per-second rates.
/// </summary>
public sealed class DemoMetrics
{
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private readonly PercentileReservoir _flushMs = new();

    private long _incomingUpdates;
    private long _notifications;
    private long _schedulerPosts;

    // Snapshot of the previous Sample() for rate deltas.
    private long _prevIncoming;
    private long _prevNotifications;
    private long _prevPosts;
    private long _prevTimestamp;

    public void OnIncomingUpdate() => Interlocked.Increment(ref _incomingUpdates);

    public void OnIncomingUpdates(long count) => Interlocked.Add(ref _incomingUpdates, count);

    public void OnNotification() => Interlocked.Increment(ref _notifications);

    public void OnSchedulerPost() => Interlocked.Increment(ref _schedulerPosts);

    public void OnFlush(long elapsedTicks) => _flushMs.Add(elapsedTicks * TicksToMs);

    public void Reset()
    {
        Interlocked.Exchange(ref _incomingUpdates, 0);
        Interlocked.Exchange(ref _notifications, 0);
        Interlocked.Exchange(ref _schedulerPosts, 0);
        _prevIncoming = _prevNotifications = _prevPosts = 0;
        _prevTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>Computes per-second rates since the previous call.</summary>
    public MetricsSample Sample()
    {
        long now = Stopwatch.GetTimestamp();
        long incoming = Interlocked.Read(ref _incomingUpdates);
        long notifications = Interlocked.Read(ref _notifications);
        long posts = Interlocked.Read(ref _schedulerPosts);

        double seconds = _prevTimestamp == 0 ? 1.0 : (now - _prevTimestamp) * TicksToMs / 1000.0;
        if (seconds <= 0)
        {
            seconds = 1.0;
        }

        double incomingRate = (incoming - _prevIncoming) / seconds;
        double notifyRate = (notifications - _prevNotifications) / seconds;
        double postRate = (posts - _prevPosts) / seconds;

        double coalescing = incomingRate <= 0 ? 0d : 1.0 - (notifyRate / incomingRate);
        coalescing = Math.Clamp(coalescing, 0d, 1d);

        _prevIncoming = incoming;
        _prevNotifications = notifications;
        _prevPosts = posts;
        _prevTimestamp = now;

        return new MetricsSample(
            incomingRate,
            notifyRate,
            postRate,
            coalescing,
            _flushMs.Percentile(0.50),
            _flushMs.Percentile(0.95),
            _flushMs.Percentile(0.99));
    }
}

/// <summary>One second's worth of derived rates and flush-duration percentiles.</summary>
public readonly record struct MetricsSample(
    double IncomingPerSecond,
    double NotificationsPerSecond,
    double SchedulerPostsPerSecond,
    double CoalescingRatio,
    double FlushP50Ms,
    double FlushP95Ms,
    double FlushP99Ms);
