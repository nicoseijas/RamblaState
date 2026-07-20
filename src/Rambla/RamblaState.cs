using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Rambla.Scheduling;

namespace Rambla;

/// <summary>
/// Base class for view models whose state is written from background threads at
/// high frequency. Rambla separates <em>state mutation</em> from <em>UI
/// notification</em>: writes mark properties dirty from any thread, and a single
/// coalesced flush is scheduled onto the UI context, where <see
/// cref="INotifyPropertyChanged.PropertyChanged"/> is raised once per property.
/// </summary>
/// <remarks>
/// <para>
/// A batch (<see cref="BeginUpdate"/>) provides <b>notification coherence</b>:
/// observers are never notified mid-batch. It does <b>not</b> provide
/// cross-thread <b>state atomicity</b> — a reader on another thread can still see
/// a new value beside a stale one before the batch closes. Use immutable
/// snapshots when you need snapshot consistency.
/// </para>
/// <para>Threading: all members are safe to call from any thread.</para>
/// <para>
/// Frozen V1 contracts (see SEMANTICS.md): notification order within a flush is
/// unspecified; a subscriber that throws fails fast but never wedges the engine;
/// <c>SetField</c> uses <see cref="EqualityComparer{T}.Default"/> and only a real
/// change counts as a mutation; the type owns no resources and is not
/// <see cref="IDisposable"/> — scheduler lifetime is the caller's.
/// </para>
/// </remarks>
public abstract class RamblaState : INotifyPropertyChanged
{
    private readonly IStateScheduler _scheduler;
    private readonly bool _collectMetrics;
    private readonly object _gate = new();
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);

    private int _batchDepth;
    private bool _flushScheduled;

    private long _mutations;
    private long _flushes;
    private long _notifications;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <param name="scheduler">
    /// The scheduler that marshals flushes to the UI. Defaults to
    /// <see cref="RamblaOptions.Default"/>'s scheduler.
    /// </param>
    /// <param name="collectMetrics">
    /// Whether to collect lifetime <see cref="StateMetrics"/>. Defaults to
    /// <see cref="RamblaOptions.CollectMetrics"/>.
    /// </param>
    protected RamblaState(IStateScheduler? scheduler = null, bool? collectMetrics = null)
    {
        _scheduler = scheduler ?? RamblaOptions.Default.Scheduler;
        _collectMetrics = collectMetrics ?? RamblaOptions.Default.CollectMetrics;
    }

    /// <summary>
    /// A snapshot of lifetime counters. Meaningful only when metrics collection
    /// is enabled; otherwise every counter reads zero.
    /// </summary>
    public StateMetrics Metrics => new(
        Interlocked.Read(ref _mutations),
        Interlocked.Read(ref _flushes),
        Interlocked.Read(ref _notifications));

    /// <summary>
    /// Assigns <paramref name="value"/> to <paramref name="field"/> if it changed,
    /// marks the property dirty, and (unless inside a batch) schedules a flush.
    /// Safe to call from any thread.
    /// </summary>
    /// <returns><see langword="true"/> if the value changed; otherwise <see langword="false"/>.</returns>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (propertyName is null)
        {
            throw new ArgumentNullException(nameof(propertyName));
        }

        bool schedule;
        lock (_gate)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            _dirty.Add(propertyName);
            schedule = TryArmFlushNoLock();
        }

        if (_collectMetrics)
        {
            Interlocked.Increment(ref _mutations);
        }

        // Post outside the lock: an ImmediateStateScheduler runs the flush
        // synchronously, and raising notifications must never happen while _gate
        // is held (reentrancy / deadlock risk if a handler touches the state).
        if (schedule)
        {
            _scheduler.Post(Flush);
        }

        return true;
    }

    /// <summary>
    /// Opens a batch: property writes are accumulated without scheduling a flush
    /// until the returned scope is disposed. The batch is coherent to the UI — the
    /// flush raises all accumulated notifications in one pass, so observers are
    /// never notified mid-batch. This is notification coherence, not cross-thread
    /// state atomicity. Batches nest; scopes may be disposed in any order.
    /// </summary>
    public IDisposable BeginUpdate()
    {
        lock (_gate)
        {
            _batchDepth++;
        }

        return new UpdateScope(this);
    }

    /// <summary>Runs <paramref name="mutate"/> inside a single <see cref="BeginUpdate"/> batch.</summary>
    public void Update(Action mutate)
    {
        if (mutate is null)
        {
            throw new ArgumentNullException(nameof(mutate));
        }

        using (BeginUpdate())
        {
            mutate();
        }
    }

    private void EndUpdate()
    {
        bool schedule;
        lock (_gate)
        {
            schedule = --_batchDepth == 0 && _dirty.Count > 0 && TryArmFlushNoLock();
        }

        if (schedule)
        {
            _scheduler.Post(Flush);
        }
    }

    /// <summary>
    /// Marks a flush as pending if one is not already armed. Must be called under
    /// <see cref="_gate"/>. Returns whether the caller now owns posting the flush.
    /// </summary>
    private bool TryArmFlushNoLock()
    {
        if (_flushScheduled || _batchDepth > 0)
        {
            return false;
        }

        _flushScheduled = true;
        return true;
    }

    private void Flush()
    {
        string[] names;

        lock (_gate)
        {
            // Clearing the dirty set and lowering the flag happen together under
            // the lock, so a write that lands after this point re-arms a fresh
            // flush and cannot be lost.
            _flushScheduled = false;

            if (_dirty.Count == 0)
            {
                return;
            }

            names = new string[_dirty.Count];
            _dirty.CopyTo(names);
            _dirty.Clear();
        }

        if (_collectMetrics)
        {
            Interlocked.Increment(ref _flushes);
        }

        PropertyChangedEventHandler? handler = PropertyChanged;
        if (handler is null)
        {
            return;
        }

        foreach (string name in names)
        {
            handler(this, new PropertyChangedEventArgs(name));
        }

        if (_collectMetrics)
        {
            Interlocked.Add(ref _notifications, names.Length);
        }
    }

    private sealed class UpdateScope : IDisposable
    {
        private RamblaState? _owner;

        public UpdateScope(RamblaState owner) => _owner = owner;

        public void Dispose()
        {
            RamblaState? owner = Interlocked.Exchange(ref _owner, null);
            owner?.EndUpdate();
        }
    }
}
