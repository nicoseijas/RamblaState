using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Rambla.Diagnostics;

/// <summary>
/// A live diagnostics session over one <see cref="RamblaState"/>. Obtain it from
/// <see cref="StateDiagnostics.Attach"/>,
/// poll <see cref="Snapshot"/> (e.g. once a second) for a reading, and dispose it
/// to detach. Thread-safe: the state reports mutations and flushes from arbitrary
/// threads while a UI thread polls snapshots.
/// </summary>
public sealed class DiagnosticsSession : IStateProbe, IDisposable
{
    private static readonly double StopwatchTicksPerSecond = Stopwatch.Frequency;

    private readonly IClock _clock;
    private readonly DiagnosticsOptions _options;
    private readonly DiagnosticsScheduler? _scheduler;
    private readonly string _stateName;
    private readonly object _snapshotGate = new();
    private readonly Dictionary<string, long[]> _lastByProperty = new(StringComparer.Ordinal);
    private readonly ConcurrentCounters _byProperty = new();

    private long _mutations;
    private long _notifications;
    private long _flushes;
    private long _raiseTicksSum;    // TimeSpan ticks
    private long _longestRaiseTicks; // TimeSpan ticks

    private IDisposable? _detach;
    private DateTimeOffset _lastTime;
    private long _lastMutations;
    private long _lastNotifications;
    private long _lastFlushes;
    private long _lastRaiseTicksSum;
    private DispatcherCounters _lastDispatcher;

    internal DiagnosticsSession(string stateName, DiagnosticsScheduler? scheduler, IClock clock, DiagnosticsOptions options)
    {
        _stateName = stateName;
        _scheduler = scheduler;
        _clock = clock;
        _options = options;
    }

    internal void Attach(RamblaState state)
    {
        _lastTime = _clock.UtcNow;
        _lastDispatcher = _scheduler?.ReadCounters() ?? default;
        _detach = state.AttachProbe(this);
    }

    /// <inheritdoc />
    public void OnMutation(string propertyName)
    {
        Interlocked.Increment(ref _mutations);
        _byProperty.IncrementMutation(propertyName);
    }

    /// <inheritdoc />
    public void OnFlush(IReadOnlyList<string> notifiedProperties, TimeSpan raiseDuration)
    {
        Interlocked.Increment(ref _flushes);
        Interlocked.Add(ref _notifications, notifiedProperties.Count);
        Interlocked.Add(ref _raiseTicksSum, raiseDuration.Ticks);
        UpdateMax(ref _longestRaiseTicks, raiseDuration.Ticks);

        for (int i = 0; i < notifiedProperties.Count; i++)
        {
            _byProperty.IncrementNotification(notifiedProperties[i]);
        }
    }

    /// <summary>
    /// Reads the diagnostics accumulated since the previous call (or since attach
    /// for the first call). Rates are computed over that elapsed window.
    /// </summary>
    public DiagnosticsSnapshot Snapshot()
    {
        lock (_snapshotGate)
        {
            DateTimeOffset now = _clock.UtcNow;
            double seconds = (now - _lastTime).TotalSeconds;

            long mutations = Interlocked.Read(ref _mutations);
            long notifications = Interlocked.Read(ref _notifications);
            long flushes = Interlocked.Read(ref _flushes);
            long raiseTicks = Interlocked.Read(ref _raiseTicksSum);

            long dMutations = mutations - _lastMutations;
            long dNotifications = notifications - _lastNotifications;
            long dFlushes = flushes - _lastFlushes;
            long dRaiseTicks = raiseTicks - _lastRaiseTicksSum;

            double mutationsPerSecond = PerSecond(dMutations, seconds);
            double notificationsPerSecond = PerSecond(dNotifications, seconds);
            double flushesPerSecond = PerSecond(dFlushes, seconds);
            double coalescingRatio = Coalescing(dMutations, dNotifications);

            bool hasDispatcher = _scheduler is not null;
            DispatcherCounters dispatcher = _scheduler?.ReadCounters() ?? default;

            double hopsPerSecond;
            TimeSpan avgLatency;
            TimeSpan peakLatency;
            TimeSpan longestFlush;
            double uiThreadBudget;

            if (hasDispatcher)
            {
                long dHops = dispatcher.Hops - _lastDispatcher.Hops;
                long dLatency = dispatcher.LatencyTicks - _lastDispatcher.LatencyTicks;
                long dFlushExec = dispatcher.FlushTicks - _lastDispatcher.FlushTicks;

                hopsPerSecond = PerSecond(dHops, seconds);
                avgLatency = dHops > 0 ? StopwatchTicks(dLatency / dHops) : TimeSpan.Zero;
                peakLatency = StopwatchTicks(dispatcher.MaxLatencyTicks);
                longestFlush = StopwatchTicks(dispatcher.MaxFlushTicks);
                uiThreadBudget = seconds > 0d ? Clamp01(StopwatchSeconds(dFlushExec) / seconds) : 0d;
            }
            else
            {
                hopsPerSecond = 0d;
                avgLatency = TimeSpan.Zero;
                peakLatency = TimeSpan.Zero;
                longestFlush = TimeSpan.FromTicks(Interlocked.Read(ref _longestRaiseTicks));
                double raiseSeconds = TimeSpan.FromTicks(dRaiseTicks).TotalSeconds;
                uiThreadBudget = seconds > 0d ? Clamp01(raiseSeconds / seconds) : 0d;
            }

            IReadOnlyList<HotProperty> hot = BuildHotProperties(seconds);
            IReadOnlyList<Recommendation> recommendations = BuildRecommendations(hot, uiThreadBudget);

            _lastTime = now;
            _lastMutations = mutations;
            _lastNotifications = notifications;
            _lastFlushes = flushes;
            _lastRaiseTicksSum = raiseTicks;
            _lastDispatcher = dispatcher;

            return new DiagnosticsSnapshot(
                _stateName,
                TimeSpan.FromSeconds(seconds),
                mutations,
                notifications,
                mutationsPerSecond,
                notificationsPerSecond,
                flushesPerSecond,
                coalescingRatio,
                longestFlush,
                uiThreadBudget,
                hasDispatcher,
                hopsPerSecond,
                avgLatency,
                peakLatency,
                hot,
                recommendations);
        }
    }

    /// <summary>Detaches the session from the state. Idempotent.</summary>
    public void Dispose()
    {
        IDisposable? detach = Interlocked.Exchange(ref _detach, null);
        detach?.Dispose();
    }

    private List<HotProperty> BuildHotProperties(double seconds)
    {
        var result = new List<HotProperty>();
        foreach (KeyValuePair<string, long[]> entry in _byProperty.Snapshot())
        {
            string name = entry.Key;
            long mutations = entry.Value[0];
            long notifications = entry.Value[1];

            if (!_lastByProperty.TryGetValue(name, out long[]? last))
            {
                last = new long[2];
            }

            long dMutations = mutations - last[0];
            long dNotifications = notifications - last[1];
            _lastByProperty[name] = new[] { mutations, notifications };

            if (dMutations == 0 && dNotifications == 0)
            {
                continue;
            }

            result.Add(new HotProperty(
                name,
                PerSecond(dMutations, seconds),
                PerSecond(dNotifications, seconds),
                Coalescing(dMutations, dNotifications)));
        }

        result.Sort(static (a, b) => b.NotificationsPerSecond.CompareTo(a.NotificationsPerSecond));
        if (result.Count > _options.MaxHotProperties)
        {
            result.RemoveRange(_options.MaxHotProperties, result.Count - _options.MaxHotProperties);
        }

        return result;
    }

    private List<Recommendation> BuildRecommendations(IReadOnlyList<HotProperty> hot, double uiThreadBudget)
    {
        var result = new List<Recommendation>();

        foreach (HotProperty p in hot)
        {
            if (p.NotificationsPerSecond > _options.HotNotificationsPerSecond)
            {
                result.Add(new Recommendation(
                    RecommendationSeverity.Warning,
                    $"'{p.Name}' generated {p.NotificationsPerSecond:N0} notifications/sec. " +
                    "Recommendation: batch related writes with BeginUpdate(), or for a collection use Batch()/ReplaceSnapshot()."));
            }
        }

        if (uiThreadBudget > _options.UiBudgetWarningFraction)
        {
            result.Add(new Recommendation(
                RecommendationSeverity.Warning,
                $"The UI thread spent {uiThreadBudget:P0} of the window flushing. " +
                "Recommendation: batch writes or lower the refresh rate."));
        }

        return result;
    }

    private static double PerSecond(long delta, double seconds)
        => seconds > 0d ? delta / seconds : 0d;

    private static double Coalescing(long mutations, long notifications)
    {
        if (mutations <= 0)
        {
            return 0d;
        }

        long coalesced = mutations - notifications;
        if (coalesced < 0)
        {
            coalesced = 0;
        }

        return (double)coalesced / mutations;
    }

    private static double Clamp01(double value)
        => value < 0d ? 0d : value > 1d ? 1d : value;

    private static TimeSpan StopwatchTicks(long ticks)
        => TimeSpan.FromSeconds(ticks / StopwatchTicksPerSecond);

    private static double StopwatchSeconds(long ticks)
        => ticks / StopwatchTicksPerSecond;

    private static void UpdateMax(ref long target, long value)
    {
        long current = Interlocked.Read(ref target);
        while (value > current)
        {
            long observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    /// <summary>
    /// A small concurrent counter table: per property, [0] = mutations, [1] =
    /// notifications, each bumped with <see cref="Interlocked"/>.
    /// </summary>
    private sealed class ConcurrentCounters
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long[]> _counts
            = new(StringComparer.Ordinal);

        public void IncrementMutation(string name)
            => Interlocked.Increment(ref _counts.GetOrAdd(name, static _ => new long[2])[0]);

        public void IncrementNotification(string name)
            => Interlocked.Increment(ref _counts.GetOrAdd(name, static _ => new long[2])[1]);

        public IEnumerable<KeyValuePair<string, long[]>> Snapshot()
        {
            foreach (KeyValuePair<string, long[]> entry in _counts)
            {
                yield return new KeyValuePair<string, long[]>(
                    entry.Key,
                    new[] { Interlocked.Read(ref entry.Value[0]), Interlocked.Read(ref entry.Value[1]) });
            }
        }
    }
}
