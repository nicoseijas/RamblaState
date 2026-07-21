using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using Rambla.Scheduling;

namespace Rambla;

/// <summary>
/// A high-frequency observable list. Like <see cref="RamblaState"/>, it separates
/// <em>mutation</em> from <em>notification</em>: writes from any thread accumulate
/// into a pending target, and a single coalesced flush transforms the UI-visible
/// contents to match — raising the minimum number of <see cref="CollectionChanged"/>
/// events (or one <see cref="NotifyCollectionChangedAction.Reset"/> when a flush
/// rewrites more than <see cref="ResetThreshold"/> elements).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads reflect the UI-visible state.</b> <see cref="Count"/>, the indexer, and
/// enumeration return the last flushed contents — never the pending target — so the
/// collection is always consistent with the change events already raised (WPF
/// requires this). A write is not visible to a reader until the next flush.
/// </para>
/// <para>
/// <b>Threading.</b> Mutators are safe to call from any thread. Reads and the
/// raised events belong to the scheduler's thread (the UI thread under a dispatcher
/// adapter); enumerate on that thread, as you would an <c>ObservableCollection</c>.
/// </para>
/// <para>
/// <b>Diffing.</b> The flush diffs by <see cref="EqualityComparer{T}.Default"/>.
/// Reordering the same instances is reported as replacements, not moves (V1). Keep
/// stable row instances (e.g. <see cref="RamblaState"/> rows) and let their fields
/// update independently; the list itself then changes only on add/remove.
/// </para>
/// <para>This is not a reactive query engine (that is DynamicData); it only makes
/// the collection → UI boundary cheap under load.</para>
/// </remarks>
public sealed class RamblaList<T> : IReadOnlyList<T>, IList, INotifyCollectionChanged, INotifyPropertyChanged
{
    private const string CountName = "Count";
    private const string IndexerName = "Item[]";

    private static readonly PropertyChangedEventArgs CountChangedArgs = new(CountName);
    private static readonly PropertyChangedEventArgs IndexerChangedArgs = new(IndexerName);

    private readonly IStateScheduler _scheduler;
    private readonly object _gate = new();
    private readonly object _syncRoot = new();
    private readonly List<T> _target = new(); // pending intended contents (any thread, under _gate)
    private readonly List<T> _view = new();    // UI-visible contents (only the active flusher touches it)

    private int _batchDepth;
    private bool _flushScheduled;
    private bool _dirty;
    private bool _flushing; // true while a flush is applying to _view; serializes flush execution

    /// <summary>Raised, coalesced per flush, when the visible contents change.</summary>
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>Raised for <c>Count</c> and the indexer when the contents change.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <param name="scheduler">Scheduler that marshals flushes to the UI; defaults to <see cref="RamblaOptions.Default"/>.</param>
    /// <param name="initial">Optional initial contents, visible immediately without a flush.</param>
    public RamblaList(IStateScheduler? scheduler = null, IEnumerable<T>? initial = null)
    {
        _scheduler = scheduler ?? RamblaOptions.Default.Scheduler;
        if (initial is not null)
        {
            foreach (T item in initial)
            {
                _target.Add(item);
                _view.Add(item);
            }
        }
    }

    /// <summary>
    /// The maximum number of element changes a single flush may apply granularly
    /// before it collapses to one <see cref="NotifyCollectionChangedAction.Reset"/>.
    /// Default 32.
    /// </summary>
    public int ResetThreshold { get; set; } = 32;

    /// <summary>The number of items currently visible (last flushed state).</summary>
    public int Count => _view.Count;

    /// <summary>The visible item at <paramref name="index"/> (last flushed state).</summary>
    public T this[int index] => _view[index];

    /// <summary>Appends <paramref name="item"/> on the next flush.</summary>
    public void Add(T item) => Mutate(list => list.Add(item));

    /// <summary>Inserts <paramref name="item"/> at <paramref name="index"/> (in the pending target) on the next flush.</summary>
    public void Insert(int index, T item) => Mutate(list => list.Insert(index, item));

    /// <summary>Removes the item at <paramref name="index"/> (in the pending target) on the next flush.</summary>
    public void RemoveAt(int index) => Mutate(list => list.RemoveAt(index));

    /// <summary>Removes the first occurrence of <paramref name="item"/> on the next flush.</summary>
    public bool Remove(T item)
    {
        bool removed = false;
        Mutate(list => removed = list.Remove(item));
        return removed;
    }

    /// <summary>Replaces the item at <paramref name="index"/> (in the pending target) on the next flush.</summary>
    public void Replace(int index, T item) => Mutate(list => list[index] = item);

    /// <summary>Clears all items on the next flush.</summary>
    public void Clear() => Mutate(list => list.Clear());

    /// <summary>
    /// Replaces the entire contents with <paramref name="items"/>; the flush diffs
    /// old against new and raises the minimum change events (its intended use).
    /// </summary>
    public void ReplaceSnapshot(IEnumerable<T> items)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        // Materialize before touching the pending target: enumerating the caller's
        // sequence can throw (or be slow, and it would run under the write lock).
        // A partially-applied replacement must never linger in the target, where a
        // later unrelated flush would silently commit it.
        List<T> snapshot = new(items);

        Mutate(list =>
        {
            list.Clear();
            list.AddRange(snapshot);
        });
    }

    /// <summary>
    /// Opens a batch: mutations accumulate without scheduling a flush until the
    /// returned scope is disposed, so a burst of writes becomes one flush.
    /// </summary>
    public IDisposable BeginUpdate()
    {
        lock (_gate)
        {
            _batchDepth++;
        }

        return new BatchScope(this);
    }

    /// <summary>Runs <paramref name="mutate"/> inside a single <see cref="BeginUpdate"/> batch.</summary>
    public void Batch(Action mutate)
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

    private void Mutate(Action<List<T>> apply)
    {
        bool schedule;
        lock (_gate)
        {
            apply(_target);
            _dirty = true;
            schedule = TryArmFlushNoLock();
        }

        if (schedule)
        {
            _scheduler.Post(Flush);
        }
    }

    private void EndUpdate()
    {
        bool schedule;
        lock (_gate)
        {
            schedule = --_batchDepth == 0 && _dirty && TryArmFlushNoLock();
        }

        if (schedule)
        {
            _scheduler.Post(Flush);
        }
    }

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
        // Serialize flush *execution*, not just scheduling. ApplyDiff mutates the
        // shared _view outside _gate, so at most one flush may run it at a time —
        // otherwise a concurrent writer (under an inline scheduler) or a reentrant
        // event handler could run a second ApplyDiff over _view mid-transition and
        // corrupt it. A flush that finds one already running bails out; the running
        // flusher drains everything via the loop below (atomic hand-off under _gate).
        lock (_gate)
        {
            _flushScheduled = false;
            // _batchDepth: a flush armed before a batch opened may be dispatched
            // while that batch is still open; delivering it would raise the
            // batch's own writes mid-batch. Defer — EndUpdate re-arms on close.
            if (_flushing || _batchDepth > 0 || !_dirty)
            {
                return;
            }

            _flushing = true;
        }

        while (true)
        {
            T[] target;
            lock (_gate)
            {
                // Also stop draining if a batch opened mid-drain: the target now
                // accumulates that batch's writes, which must not leak early.
                if (!_dirty || _batchDepth > 0)
                {
                    _flushing = false; // cleared under _gate, atomically with the empty check
                    return;
                }

                _dirty = false;
                target = _target.ToArray();
            }

            try
            {
                ApplyDiff(target);
            }
            catch
            {
                // Leave the engine flushable: a partial flush is reconciled by the
                // next flush's diff (old _view vs newest target). Fail-fast otherwise.
                lock (_gate)
                {
                    _flushing = false;
                }

                throw;
            }
        }
    }

    private void ApplyDiff(T[] target)
    {
        EqualityComparer<T> eq = EqualityComparer<T>.Default;
        int oldCount = _view.Count;
        int newCount = target.Length;

        // Trim the common prefix and suffix; the middle is what actually changed.
        int prefix = 0;
        while (prefix < oldCount && prefix < newCount && eq.Equals(_view[prefix], target[prefix]))
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < oldCount - prefix && suffix < newCount - prefix
               && eq.Equals(_view[oldCount - 1 - suffix], target[newCount - 1 - suffix]))
        {
            suffix++;
        }

        int oldMid = oldCount - prefix - suffix;
        int newMid = newCount - prefix - suffix;
        int overlap = Math.Min(oldMid, newMid);

        int replaces = 0;
        for (int i = 0; i < overlap; i++)
        {
            if (!eq.Equals(_view[prefix + i], target[prefix + i]))
            {
                replaces++;
            }
        }

        int changeCount = replaces + Math.Abs(newMid - oldMid);
        if (changeCount == 0)
        {
            return; // coalesced to nothing (e.g. an add then an undo)
        }

        NotifyCollectionChangedEventHandler? handler = CollectionChanged;

        if (changeCount > ResetThreshold)
        {
            _view.Clear();
            _view.AddRange(target);
            handler?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            RaiseCountAndIndexer(oldCount != newCount);
            return;
        }

        // Replacements within the overlapping middle.
        for (int i = 0; i < overlap; i++)
        {
            int index = prefix + i;
            if (!eq.Equals(_view[index], target[index]))
            {
                T oldItem = _view[index];
                T newItem = target[index];
                _view[index] = newItem;
                handler?.Invoke(this, new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Replace, newItem, oldItem, index));
            }
        }

        if (newMid > oldMid)
        {
            // Insert the extra new items at their target positions.
            for (int i = overlap; i < newMid; i++)
            {
                int index = prefix + i;
                T item = target[index];
                _view.Insert(index, item);
                handler?.Invoke(this, new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Add, item, index));
            }
        }
        else if (oldMid > newMid)
        {
            // Remove the surplus old items from the first non-matching position.
            int index = prefix + newMid;
            for (int i = newMid; i < oldMid; i++)
            {
                T oldItem = _view[index];
                _view.RemoveAt(index);
                handler?.Invoke(this, new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Remove, oldItem, index));
            }
        }

        RaiseCountAndIndexer(oldCount != newCount);
    }

    private void RaiseCountAndIndexer(bool countChanged)
    {
        PropertyChangedEventHandler? handler = PropertyChanged;
        if (handler is null)
        {
            return;
        }

        if (countChanged)
        {
            handler(this, CountChangedArgs);
        }

        handler(this, IndexerChangedArgs);
    }

    /// <summary>Enumerates the visible items (last flushed state).</summary>
    public IEnumerator<T> GetEnumerator() => _view.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_view).GetEnumerator();

    // --- IList (read-only to WPF, so it uses a virtualizing ListCollectionView).
    //     Mutations happen only through the thread-safe API above. ---

    bool IList.IsFixedSize => false;

    bool IList.IsReadOnly => true;

    bool ICollection.IsSynchronized => false;

    // A dedicated sentinel, not _gate: _gate guards the pending target, not _view,
    // so locking SyncRoot would not actually coordinate with the flush that mutates
    // the visible contents. IsSynchronized is false — read on the scheduler thread.
    object ICollection.SyncRoot => _syncRoot;

    object? IList.this[int index]
    {
        get => _view[index];
        set => throw new NotSupportedException("RamblaList is read-only through IList; use the typed mutators.");
    }

    bool IList.Contains(object? value) => value is T item && _view.Contains(item);

    int IList.IndexOf(object? value) => value is T item ? _view.IndexOf(item) : -1;

    void ICollection.CopyTo(Array array, int index) => ((ICollection)_view).CopyTo(array, index);

    int IList.Add(object? value) => throw new NotSupportedException("RamblaList is read-only through IList; use Add(T).");

    void IList.Clear() => throw new NotSupportedException("RamblaList is read-only through IList; use Clear().");

    void IList.Insert(int index, object? value) => throw new NotSupportedException("RamblaList is read-only through IList; use Insert(int, T).");

    void IList.Remove(object? value) => throw new NotSupportedException("RamblaList is read-only through IList; use Remove(T).");

    void IList.RemoveAt(int index) => throw new NotSupportedException("RamblaList is read-only through IList; use RemoveAt(int).");

    private sealed class BatchScope : IDisposable
    {
        private RamblaList<T>? _owner;

        public BatchScope(RamblaList<T> owner) => _owner = owner;

        public void Dispose()
        {
            RamblaList<T>? owner = Interlocked.Exchange(ref _owner, null);
            owner?.EndUpdate();
        }
    }
}
