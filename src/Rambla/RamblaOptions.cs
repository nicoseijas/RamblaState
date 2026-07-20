using Rambla.Scheduling;

namespace Rambla;

/// <summary>
/// Global configuration for <see cref="RamblaState"/> instances that do not
/// receive an explicit scheduler.
/// </summary>
public sealed class RamblaOptions
{
    /// <summary>The ambient options used when a state is created without an explicit scheduler.</summary>
    public static RamblaOptions Default { get; } = new();

    /// <summary>
    /// The scheduler that marshals coalesced flushes to the UI. Defaults to the
    /// synchronous <see cref="ImmediateStateScheduler"/>; UI adapters replace this
    /// at startup (e.g. <c>RamblaOptions.Default.Scheduler = DispatcherStateScheduler.ForCurrent();</c>).
    /// </summary>
    public IStateScheduler Scheduler { get; set; } = ImmediateStateScheduler.Instance;

    /// <summary>
    /// Upper bound, in flushes per second, that a throttling scheduler should
    /// enforce. Reserved for the coalescing scheduler (Phase 1); not yet applied
    /// by the immediate/synchronization-context schedulers.
    /// </summary>
    public int MaxRefreshRate { get; set; } = 60;

    /// <summary>
    /// Default for whether new <see cref="RamblaState"/> instances collect
    /// lifetime <see cref="StateMetrics"/>. Off by default so the hot path stays
    /// allocation- and contention-free; the demo and diagnostics turn it on.
    /// </summary>
    public bool CollectMetrics { get; set; }
}
