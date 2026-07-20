using Rambla.Scheduling;

namespace Rambla.Tests;

/// <summary>
/// A deterministic scheduler for tests: it captures posted flushes in a queue
/// instead of running them, so a test can drive the exact moment coalescing
/// resolves by calling <see cref="Drain"/>. This models an asynchronous UI
/// scheduler without a real UI thread. Thread-safe so it can be shared across
/// concurrent writers.
/// </summary>
internal sealed class ManualStateScheduler : IStateScheduler
{
    private readonly object _gate = new();
    private readonly Queue<Action> _pending = new();
    private int _postCount;

    public int PostCount => Volatile.Read(ref _postCount);

    public void Post(Action flush)
    {
        Interlocked.Increment(ref _postCount);
        lock (_gate)
        {
            _pending.Enqueue(flush);
        }
    }

    /// <summary>Runs every pending flush, in order, until the queue is empty.</summary>
    public void Drain()
    {
        while (true)
        {
            Action next;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                next = _pending.Dequeue();
            }

            next();
        }
    }
}
