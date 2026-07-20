using System;

namespace Rambla.Diagnostics;

/// <summary>
/// Entry point for Rambla diagnostics. Attach a session to a
/// <see cref="RamblaState"/> to observe — without changing its behaviour — how
/// many mutations coalescing collapsed, where the UI thread spent its time, and
/// which properties are hot.
/// </summary>
public static class StateDiagnostics
{
    /// <summary>
    /// Attaches a live <see cref="DiagnosticsSession"/> to <paramref name="state"/>.
    /// Poll <see cref="DiagnosticsSession.Snapshot"/> for readings and dispose the
    /// session to detach.
    /// </summary>
    /// <param name="state">The state to observe.</param>
    /// <param name="scheduler">
    /// Optional. The <see cref="DiagnosticsScheduler"/> the state posts through — it
    /// must be the very instance <paramref name="state"/> was constructed with, or
    /// the dispatcher figures will all read zero while still being reported as
    /// present. Pass it to include dispatcher latency, hops, and an accurate
    /// UI-thread budget; omit it and those are derived from notification-raise time.
    /// </param>
    /// <param name="clock">Optional clock; defaults to <see cref="SystemClock.Instance"/>.</param>
    /// <param name="options">Optional thresholds; defaults to <see cref="DiagnosticsOptions.Default"/>.</param>
    public static DiagnosticsSession Attach(
        RamblaState state,
        DiagnosticsScheduler? scheduler = null,
        IClock? clock = null,
        DiagnosticsOptions? options = null)
    {
        if (state is null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        var session = new DiagnosticsSession(
            state.GetType().Name,
            scheduler,
            clock ?? SystemClock.Instance,
            options ?? DiagnosticsOptions.Default);

        session.Attach(state);
        return session;
    }
}
