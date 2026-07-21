using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using Rambla.Scheduling;

namespace Rambla;

/// <summary>
/// A high-frequency observable dictionary — the keyed companion to
/// <see cref="RamblaList{T}"/>. Writes from any thread accumulate into a pending
/// target, and a single coalesced flush transforms the UI-visible contents to
/// match, raising the minimum number of <see cref="CollectionChanged"/> events
/// (or one <see cref="NotifyCollectionChangedAction.Reset"/> when a flush
/// rewrites more than <see cref="ResetThreshold"/> entries).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordered by insertion.</b> WPF needs meaningful indices in collection-change
/// events, so entries expose a stable order: new keys append, updating an existing
/// key preserves its position, and a key removed and re-added appends at the end.
/// The visible contents enumerate as ordered <see cref="KeyValuePair{TKey,TValue}"/>
/// entries — bind an <c>ItemsControl</c> directly to the dictionary.
/// </para>
/// <para>
/// <b>Reads reflect the UI-visible state.</b> <see cref="Count"/>, the key indexer,
/// <see cref="TryGetValue"/>, and enumeration return the last flushed contents —
/// never the pending target — so the collection is always consistent with the
/// change events already raised. A write is not visible to a reader until the next
/// flush. (<see cref="Add"/>/<see cref="TryAdd"/>/<see cref="Remove"/> report
/// against the <em>pending</em> target, so back-to-back writes compose sensibly.)
/// </para>
/// <para>
/// <b>Threading.</b> Mutators are safe to call from any thread. Reads and the
/// raised events belong to the scheduler's thread (the UI thread under a
/// dispatcher adapter).
/// </para>
/// <para>
/// <b>Coalescing.</b> A key written five times between flushes raises one
/// <c>Replace</c> with the final value — latest value wins per key. The flush
/// diffs by <see cref="EqualityComparer{T}.Default"/> on both key and value.
/// </para>
/// </remarks>
public sealed class RamblaDictionary<TKey, TValue> :
    IReadOnlyDictionary<TKey, TValue>,
    IReadOnlyList<KeyValuePair<TKey, TValue>>,
    IList,
    INotifyCollectionChanged,
    INotifyPropertyChanged
    where TKey : notnull
{
    private const string CountName = "Count";
    private const string IndexerName = "Item[]";

    private static readonly PropertyChangedEventArgs CountChangedArgs = new(CountName);
    private static readonly PropertyChangedEventArgs IndexerChangedArgs = new(IndexerName);

    private readonly IStateScheduler _scheduler;
    private readonly object _gate = new();
    private readonly object _syncRoot = new();

    // Pending intended contents (any thread, under _gate): insertion-ordered
    // entries plus a key → position index for O(1) keyed mutation.
    private readonly List<KeyValuePair<TKey, TValue>> _targetList = new();
    private readonly Dictionary<TKey, int> _targetIndex = new();

    // UI-visible contents (only the active flusher mutates them): the ordered
    // entries drive events/enumeration; the map serves keyed reads. Both are
    // updated together per raised event, so they never disagree mid-flush.
    private readonly List<KeyValuePair<TKey, TValue>> _viewList = new();
    private readonly Dictionary<TKey, TValue> _viewMap = new();

    private int _batchDepth;
    private bool _flushScheduled;
    private bool _dirty;
    private bool _flushing; // true while a flush is applying to the view; serializes flush execution

    /// <summary>Raised, coalesced per flush, when the visible contents change.</summary>
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>Raised for <c>Count</c> and the indexer when the contents change.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <param name="scheduler">Scheduler that marshals flushes to the UI; defaults to <see cref="RamblaOptions.Default"/>.</param>
    /// <param name="initial">Optional initial entries, visible immediately without a flush. Duplicate keys throw.</param>
    public RamblaDictionary(IStateScheduler? scheduler = null, IEnumerable<KeyValuePair<TKey, TValue>>? initial = null)
    {
        _scheduler = scheduler ?? RamblaOptions.Default.Scheduler;
        if (initial is not null)
        {
            foreach (KeyValuePair<TKey, TValue> entry in initial)
            {
                _targetIndex.Add(entry.Key, _targetList.Count); // throws on duplicates
                _targetList.Add(entry);
                _viewList.Add(entry);
                _viewMap.Add(entry.Key, entry.Value);
            }
        }
    }

    /// <summary>
    /// The maximum number of entry changes a single flush may apply granularly
    /// before it collapses to one <see cref="NotifyCollectionChangedAction.Reset"/>.
    /// Default 32.
    /// </summary>
    public int ResetThreshold { get; set; } = 32;

    /// <summary>The number of entries currently visible (last flushed state).</summary>
    public int Count => _viewList.Count;

    /// <summary>
    /// Gets the visible value for <paramref name="key"/> (last flushed state), or
    /// sets it in the pending target — adding the key (appended in order) if absent,
    /// updating it in place otherwise.
    /// </summary>
    public TValue this[TKey key]
    {
        get => _viewMap[key];
        set => Mutate(key, value, addOrUpdate: true, throwIfPresent: false, out _);
    }

    /// <summary>Whether <paramref name="key"/> is visible (last flushed state).</summary>
    public bool ContainsKey(TKey key) => _viewMap.ContainsKey(key);

    /// <summary>Gets the visible value for <paramref name="key"/> (last flushed state).</summary>
    public bool TryGetValue(TKey key, out TValue value) => _viewMap.TryGetValue(key, out value!);

    /// <summary>The visible keys, in entry order (last flushed state).</summary>
    public IEnumerable<TKey> Keys
    {
        get
        {
            foreach (KeyValuePair<TKey, TValue> entry in _viewList)
            {
                yield return entry.Key;
            }
        }
    }

    /// <summary>The visible values, in entry order (last flushed state).</summary>
    public IEnumerable<TValue> Values
    {
        get
        {
            foreach (KeyValuePair<TKey, TValue> entry in _viewList)
            {
                yield return entry.Value;
            }
        }
    }

    /// <summary>
    /// Adds <paramref name="key"/> on the next flush; throws
    /// <see cref="ArgumentException"/> if it is already in the pending target.
    /// </summary>
    public void Add(TKey key, TValue value) => Mutate(key, value, addOrUpdate: false, throwIfPresent: true, out _);

    /// <summary>
    /// Adds <paramref name="key"/> on the next flush unless it is already in the
    /// pending target; returns whether it was added.
    /// </summary>
    public bool TryAdd(TKey key, TValue value)
    {
        Mutate(key, value, addOrUpdate: false, throwIfPresent: false, out bool added);
        return added;
    }

    /// <summary>
    /// Removes <paramref name="key"/> on the next flush; returns whether it was
    /// present in the pending target.
    /// </summary>
    public bool Remove(TKey key)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        bool schedule;
        lock (_gate)
        {
            if (!_targetIndex.TryGetValue(key, out int index))
            {
                return false;
            }

            _targetList.RemoveAt(index);
            _targetIndex.Remove(key);
            for (int i = index; i < _targetList.Count; i++)
            {
                _targetIndex[_targetList[i].Key] = i;
            }

            _dirty = true;
            schedule = TryArmFlushNoLock();
        }

        if (schedule)
        {
            _scheduler.Post(Flush);
        }

        return true;
    }

    /// <summary>Clears all entries on the next flush.</summary>
    public void Clear()
    {
        bool schedule;
        lock (_gate)
        {
            _targetList.Clear();
            _targetIndex.Clear();
            _dirty = true;
            schedule = TryArmFlushNoLock();
        }

        if (schedule)
        {
            _scheduler.Post(Flush);
        }
    }

    /// <summary>
    /// Replaces the entire contents with <paramref name="entries"/>; the flush diffs
    /// old against new and raises the minimum change events. Duplicate keys throw,
    /// leaving the pending target untouched.
    /// </summary>
    public void ReplaceSnapshot(IEnumerable<KeyValuePair<TKey, TValue>> entries)
    {
        if (entries is null)
        {
            throw new ArgumentNullException(nameof(entries));
        }

        // Materialize and validate before touching the pending target: enumerating
        // the caller's sequence can throw, and a duplicate key must be rejected
        // whole. A partially-applied replacement must never linger in the target,
        // where a later unrelated flush would silently commit it.
        List<KeyValuePair<TKey, TValue>> snapshotList = new();
        Dictionary<TKey, int> snapshotIndex = new();
        foreach (KeyValuePair<TKey, TValue> entry in entries)
        {
            snapshotIndex.Add(entry.Key, snapshotList.Count); // throws on duplicates
            snapshotList.Add(entry);
        }

        bool schedule;
        lock (_gate)
        {
            _targetList.Clear();
            _targetList.AddRange(snapshotList);
            _targetIndex.Clear();
            foreach (KeyValuePair<TKey, int> entry in snapshotIndex)
            {
                _targetIndex.Add(entry.Key, entry.Value);
            }

            _dirty = true;
            schedule = TryArmFlushNoLock();
        }

        if (schedule)
        {
            _scheduler.Post(Flush);
        }
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

    private void Mutate(TKey key, TValue value, bool addOrUpdate, bool throwIfPresent, out bool added)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        bool schedule;
        lock (_gate)
        {
            if (_targetIndex.TryGetValue(key, out int index))
            {
                if (throwIfPresent)
                {
                    throw new ArgumentException($"An entry with the same key already exists (pending target): {key}", nameof(key));
                }

                if (!addOrUpdate)
                {
                    added = false;
                    return; // TryAdd on an existing key: no mutation, no flush
                }

                _targetList[index] = new KeyValuePair<TKey, TValue>(key, value);
                added = false;
            }
            else
            {
                _targetIndex.Add(key, _targetList.Count);
                _targetList.Add(new KeyValuePair<TKey, TValue>(key, value));
                added = true;
            }

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
        // Serialize flush *execution*, not just scheduling: ApplyDiff mutates the
        // shared view outside _gate, so at most one flush may run it at a time.
        // A flush that finds one already running bails out; the running flusher
        // drains everything via the loop below (atomic hand-off under _gate).
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
            KeyValuePair<TKey, TValue>[] target;
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
                target = _targetList.ToArray();
            }

            try
            {
                ApplyDiff(target);
            }
            catch
            {
                // Leave the engine flushable: a partial flush is reconciled by the
                // next flush's diff (old view vs newest target). Fail-fast otherwise.
                lock (_gate)
                {
                    _flushing = false;
                }

                throw;
            }
        }
    }

    private void ApplyDiff(KeyValuePair<TKey, TValue>[] target)
    {
        EqualityComparer<TKey> keyEq = EqualityComparer<TKey>.Default;
        EqualityComparer<TValue> valueEq = EqualityComparer<TValue>.Default;
        int oldCount = _viewList.Count;
        int newCount = target.Length;

        bool EntryEquals(KeyValuePair<TKey, TValue> a, KeyValuePair<TKey, TValue> b)
            => keyEq.Equals(a.Key, b.Key) && valueEq.Equals(a.Value, b.Value);

        // Trim the common prefix and suffix; the middle is what actually changed.
        int prefix = 0;
        while (prefix < oldCount && prefix < newCount && EntryEquals(_viewList[prefix], target[prefix]))
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < oldCount - prefix && suffix < newCount - prefix
               && EntryEquals(_viewList[oldCount - 1 - suffix], target[newCount - 1 - suffix]))
        {
            suffix++;
        }

        int oldMid = oldCount - prefix - suffix;
        int newMid = newCount - prefix - suffix;
        int overlap = Math.Min(oldMid, newMid);

        int replaces = 0;
        for (int i = 0; i < overlap; i++)
        {
            if (!EntryEquals(_viewList[prefix + i], target[prefix + i]))
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
            _viewList.Clear();
            _viewList.AddRange(target);
            _viewMap.Clear();
            foreach (KeyValuePair<TKey, TValue> entry in target)
            {
                _viewMap.Add(entry.Key, entry.Value);
            }

            handler?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            RaiseCountAndIndexer(oldCount != newCount);
            return;
        }

        // Replacements within the overlapping middle. The map tracks the view
        // list per event so keyed reads converge with the raised events.
        for (int i = 0; i < overlap; i++)
        {
            int index = prefix + i;
            if (!EntryEquals(_viewList[index], target[index]))
            {
                KeyValuePair<TKey, TValue> oldEntry = _viewList[index];
                KeyValuePair<TKey, TValue> newEntry = target[index];
                _viewList[index] = newEntry;
                if (!keyEq.Equals(oldEntry.Key, newEntry.Key))
                {
                    MapRemoveUnlessStillListed(oldEntry.Key);
                }

                _viewMap[newEntry.Key] = newEntry.Value;
                handler?.Invoke(this, new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Replace, newEntry, oldEntry, index));
            }
        }

        if (newMid > oldMid)
        {
            // Insert the extra new entries at their target positions.
            for (int i = overlap; i < newMid; i++)
            {
                int index = prefix + i;
                KeyValuePair<TKey, TValue> entry = target[index];
                _viewList.Insert(index, entry);
                _viewMap[entry.Key] = entry.Value;
                handler?.Invoke(this, new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Add, entry, index));
            }
        }
        else if (oldMid > newMid)
        {
            // Remove the surplus old entries from the first non-matching position.
            int index = prefix + newMid;
            for (int i = newMid; i < oldMid; i++)
            {
                KeyValuePair<TKey, TValue> oldEntry = _viewList[index];
                _viewList.RemoveAt(index);
                MapRemoveUnlessStillListed(oldEntry.Key);
                handler?.Invoke(this, new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Remove, oldEntry, index));
            }
        }

        RaiseCountAndIndexer(oldCount != newCount);
    }

    private void MapRemoveUnlessStillListed(TKey key)
    {
        // A misaligned positional diff can transiently duplicate a key in the
        // view list: an entry replaced at one index may still sit at a later
        // index until a subsequent event resolves it. Drop the key from the map
        // only when no remaining listed entry carries it — otherwise a key that
        // survives the flush at another position would vanish from keyed reads.
        // The scan is bounded: granular diffs raise at most ResetThreshold events.
        EqualityComparer<TKey> keyEq = EqualityComparer<TKey>.Default;
        for (int i = 0; i < _viewList.Count; i++)
        {
            if (keyEq.Equals(_viewList[i].Key, key))
            {
                return;
            }
        }

        _viewMap.Remove(key);
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

    /// <summary>Enumerates the visible entries in order (last flushed state).</summary>
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _viewList.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_viewList).GetEnumerator();

    // Explicit so it cannot collide with the key indexer when TKey is int.
    KeyValuePair<TKey, TValue> IReadOnlyList<KeyValuePair<TKey, TValue>>.this[int index] => _viewList[index];

    // --- IList (read-only to WPF, so it uses a virtualizing ListCollectionView).
    //     Mutations happen only through the thread-safe API above. ---

    bool IList.IsFixedSize => false;

    bool IList.IsReadOnly => true;

    bool ICollection.IsSynchronized => false;

    // A dedicated sentinel, not _gate: _gate guards the pending target, not the
    // view, so locking SyncRoot would not coordinate with the flush that mutates
    // the visible contents. IsSynchronized is false — read on the scheduler thread.
    object ICollection.SyncRoot => _syncRoot;

    object? IList.this[int index]
    {
        get => _viewList[index];
        set => throw new NotSupportedException("RamblaDictionary is read-only through IList; use the typed mutators.");
    }

    bool IList.Contains(object? value)
        => value is KeyValuePair<TKey, TValue> entry
           && _viewMap.TryGetValue(entry.Key, out TValue? current)
           && EqualityComparer<TValue>.Default.Equals(current, entry.Value);

    int IList.IndexOf(object? value) => value is KeyValuePair<TKey, TValue> entry ? _viewList.IndexOf(entry) : -1;

    void ICollection.CopyTo(Array array, int index) => ((ICollection)_viewList).CopyTo(array, index);

    int IList.Add(object? value) => throw new NotSupportedException("RamblaDictionary is read-only through IList; use Add(TKey, TValue).");

    void IList.Clear() => throw new NotSupportedException("RamblaDictionary is read-only through IList; use Clear().");

    void IList.Insert(int index, object? value) => throw new NotSupportedException("RamblaDictionary is read-only through IList; entries are ordered by insertion.");

    void IList.Remove(object? value) => throw new NotSupportedException("RamblaDictionary is read-only through IList; use Remove(TKey).");

    void IList.RemoveAt(int index) => throw new NotSupportedException("RamblaDictionary is read-only through IList; use Remove(TKey).");

    private sealed class BatchScope : IDisposable
    {
        private RamblaDictionary<TKey, TValue>? _owner;

        public BatchScope(RamblaDictionary<TKey, TValue> owner) => _owner = owner;

        public void Dispose()
        {
            RamblaDictionary<TKey, TValue>? owner = Interlocked.Exchange(ref _owner, null);
            owner?.EndUpdate();
        }
    }
}
