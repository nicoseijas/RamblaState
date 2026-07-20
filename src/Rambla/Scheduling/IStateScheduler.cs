namespace Rambla.Scheduling;

/// <summary>
/// Marshals a coalesced state flush onto the surface that owns UI notifications
/// (e.g. the WPF <c>Dispatcher</c> thread). The core never references a UI
/// framework directly: every hop to the UI goes through this abstraction.
/// </summary>
public interface IStateScheduler
{
    /// <summary>
    /// Requests that <paramref name="flush"/> run on the UI context.
    /// <see cref="Rambla.RamblaState"/> guarantees it only posts one pending
    /// flush at a time, so implementations do not need to de-duplicate.
    /// </summary>
    void Post(Action flush);
}
