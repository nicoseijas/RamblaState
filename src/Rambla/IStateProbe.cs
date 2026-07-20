using System;
using System.Collections.Generic;

namespace Rambla;

/// <summary>
/// A pure observer of a <see cref="RamblaState"/>'s internal activity, used to
/// build diagnostics without changing behaviour. Attach one with
/// <see cref="RamblaState.AttachProbe"/>.
/// </summary>
/// <remarks>
/// <para>
/// A probe is invoked <b>after the fact</b> and must not throw, block, or mutate
/// the state — it observes, it does not participate. Attaching a probe does not
/// change any frozen V1 contract (notification order, coherence, coalescing, or
/// exception behaviour): the engine calls the probe only to report what already
/// happened.
/// </para>
/// <para>
/// Callbacks arrive from arbitrary threads (a mutation is reported on the writing
/// thread; a flush on the scheduler's thread), so an implementation must be
/// thread-safe. Keep the work trivial — a probe runs on the hot path. A probe
/// that throws is isolated by the engine (its exception is swallowed so it cannot
/// change behaviour), but that is a safety net, not license to throw.
/// </para>
/// <para>
/// No cross-thread ordering is guaranteed between callbacks: under concurrent
/// writers, <see cref="OnFlush"/> may report a property before the
/// <see cref="OnMutation"/> for that same change has run on the other thread. Do
/// not build a probe that depends on seeing a property's mutation before its
/// flush; treat each callback as an independent event.
/// </para>
/// </remarks>
public interface IStateProbe
{
    /// <summary>Reports one accepted value change (a real mutation; no-op writes are not reported).</summary>
    /// <param name="propertyName">The property that changed.</param>
    void OnMutation(string propertyName);

    /// <summary>
    /// Reports one coalesced flush that raised notifications.
    /// </summary>
    /// <param name="notifiedProperties">
    /// The distinct properties notified in this flush. Do not retain the reference
    /// beyond the call; its contents are owned by the engine.
    /// </param>
    /// <param name="raiseDuration">Wall-clock time spent raising the notifications.</param>
    void OnFlush(IReadOnlyList<string> notifiedProperties, TimeSpan raiseDuration);
}
