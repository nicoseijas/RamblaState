using System;
using System.Diagnostics;
using System.Threading;
using Rambla.Scheduling;

namespace Rambla.Diagnostics;

/// <summary>
/// An <see cref="IStateScheduler"/> decorator that measures the dispatcher cost
/// Rambla cannot see from inside the state: how long a flush waits between being
/// posted and actually running (<em>dispatcher latency</em>), how many dispatcher
/// hops occur, and how long each flush occupies the UI thread. Wrap your real
/// scheduler with it and pass it to <see cref="StateDiagnostics.Attach"/>.
/// </summary>
/// <remarks>
/// Timing uses <see cref="Stopwatch"/> timestamps (monotonic), independent of the
/// injected <see cref="IClock"/> which paces rate windows. All counters are
/// cumulative and read atomically.
/// </remarks>
public sealed class DiagnosticsScheduler : IStateScheduler
{
    private readonly IStateScheduler _inner;

    private long _hops;
    private long _latencyTicks;
    private long _maxLatencyTicks;
    private long _flushTicks;
    private long _maxFlushTicks;

    /// <param name="inner">The scheduler that actually marshals the flush (e.g. the WPF dispatcher adapter).</param>
    public DiagnosticsScheduler(IStateScheduler inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public void Post(Action flush)
    {
        if (flush is null)
        {
            throw new ArgumentNullException(nameof(flush));
        }

        long postedAt = Stopwatch.GetTimestamp();
        _inner.Post(() =>
        {
            long startedAt = Stopwatch.GetTimestamp();
            long latency = startedAt - postedAt;
            Interlocked.Increment(ref _hops);
            Interlocked.Add(ref _latencyTicks, latency);
            UpdateMax(ref _maxLatencyTicks, latency);

            // finally, not sequential code: a flush that throws (fail-fast
            // subscriber contract) already counted as a hop above, so its
            // execution time must land too or the UI-budget metric skews low.
            try
            {
                flush();
            }
            finally
            {
                long flushDuration = Stopwatch.GetTimestamp() - startedAt;
                Interlocked.Add(ref _flushTicks, flushDuration);
                UpdateMax(ref _maxFlushTicks, flushDuration);
            }
        });
    }

    /// <summary>Reads the cumulative dispatcher counters atomically.</summary>
    public DispatcherCounters ReadCounters() => new(
        Interlocked.Read(ref _hops),
        Interlocked.Read(ref _latencyTicks),
        Interlocked.Read(ref _maxLatencyTicks),
        Interlocked.Read(ref _flushTicks),
        Interlocked.Read(ref _maxFlushTicks));

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
}

/// <summary>Cumulative dispatcher timing counters, in <see cref="Stopwatch"/> ticks.</summary>
public readonly struct DispatcherCounters
{
    internal DispatcherCounters(long hops, long latencyTicks, long maxLatencyTicks, long flushTicks, long maxFlushTicks)
    {
        Hops = hops;
        LatencyTicks = latencyTicks;
        MaxLatencyTicks = maxLatencyTicks;
        FlushTicks = flushTicks;
        MaxFlushTicks = maxFlushTicks;
    }

    /// <summary>Total flushes dispatched (dispatcher hops).</summary>
    public long Hops { get; }

    /// <summary>Sum of post-to-run latencies, in Stopwatch ticks.</summary>
    public long LatencyTicks { get; }

    /// <summary>Largest single post-to-run latency observed, in Stopwatch ticks.</summary>
    public long MaxLatencyTicks { get; }

    /// <summary>Sum of flush execution durations, in Stopwatch ticks.</summary>
    public long FlushTicks { get; }

    /// <summary>Largest single flush execution duration observed, in Stopwatch ticks.</summary>
    public long MaxFlushTicks { get; }
}
