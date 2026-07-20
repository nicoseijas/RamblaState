using System.Threading;

namespace Rambla.Scheduling;

/// <summary>
/// Posts flushes to a captured <see cref="SynchronizationContext"/>. Because the
/// post is asynchronous, mutations that arrive before the posted flush runs are
/// coalesced into a single notification pass. This is the framework-agnostic way
/// to reach a UI thread when no dedicated adapter is available.
/// </summary>
public sealed class SynchronizationContextStateScheduler : IStateScheduler
{
    private readonly SynchronizationContext _context;

    /// <param name="context">
    /// The target context. Defaults to <see cref="SynchronizationContext.Current"/>
    /// captured at construction time.
    /// </param>
    public SynchronizationContextStateScheduler(SynchronizationContext? context = null)
    {
        _context = context
            ?? SynchronizationContext.Current
            ?? throw new InvalidOperationException(
                "No SynchronizationContext is available on the current thread. " +
                "Construct this scheduler on the UI thread or pass a context explicitly.");
    }

    /// <inheritdoc />
    public void Post(Action flush) => _context.Post(static state => ((Action)state!)(), flush);
}
