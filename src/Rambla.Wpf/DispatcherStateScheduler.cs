using System.Windows.Threading;
using Rambla.Scheduling;

namespace Rambla.Wpf;

/// <summary>
/// An <see cref="IStateScheduler"/> that marshals coalesced flushes onto the WPF
/// UI thread via a <see cref="Dispatcher"/>. Flushes are posted asynchronously,
/// which is what lets background bursts coalesce into one notification pass.
/// </summary>
public sealed class DispatcherStateScheduler : IStateScheduler
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherPriority _priority;

    /// <param name="dispatcher">The UI dispatcher to post flushes to.</param>
    /// <param name="priority">
    /// The dispatcher priority for flushes. <see cref="DispatcherPriority.Background"/>
    /// keeps high-frequency state from starving input and rendering.
    /// </param>
    public DispatcherStateScheduler(
        Dispatcher dispatcher,
        DispatcherPriority priority = DispatcherPriority.Background)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _priority = priority;
    }

    /// <summary>Creates a scheduler bound to the dispatcher of the calling (UI) thread.</summary>
    public static DispatcherStateScheduler ForCurrent() => new(Dispatcher.CurrentDispatcher);

    /// <summary>
    /// Installs a dispatcher scheduler bound to the current thread as the process
    /// default. Call once from the UI thread during application startup.
    /// </summary>
    public static void InstallAsDefault(
        DispatcherPriority priority = DispatcherPriority.Background)
        => RamblaOptions.Default.Scheduler = new DispatcherStateScheduler(
            Dispatcher.CurrentDispatcher, priority);

    /// <inheritdoc />
    public void Post(Action flush) => _dispatcher.BeginInvoke(_priority, flush);
}
