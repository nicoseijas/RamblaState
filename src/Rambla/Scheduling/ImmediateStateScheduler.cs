namespace Rambla.Scheduling;

/// <summary>
/// Runs the flush synchronously on the calling thread. This is the naive,
/// no-coalescing baseline: every mutation notifies immediately. Useful as a
/// benchmark baseline and in unit tests that want deterministic, inline flushes.
/// </summary>
public sealed class ImmediateStateScheduler : IStateScheduler
{
    /// <summary>A shared, stateless instance.</summary>
    public static ImmediateStateScheduler Instance { get; } = new();

    /// <inheritdoc />
    public void Post(Action flush) => flush();
}
