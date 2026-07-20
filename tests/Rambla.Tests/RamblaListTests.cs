using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using FluentAssertions;
using Rambla.Scheduling;
using Xunit;

namespace Rambla.Tests;

public sealed class RamblaListTests
{
    private static (RamblaList<int> list, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events)
        Build(IEnumerable<int>? initial = null)
    {
        var scheduler = new ManualStateScheduler();
        var list = new RamblaList<int>(scheduler, initial);
        var events = new List<NotifyCollectionChangedEventArgs>();
        list.CollectionChanged += (_, e) => events.Add(e);
        return (list, scheduler, events);
    }

    [Fact]
    public void Writes_are_not_visible_until_the_flush()
    {
        (RamblaList<int> list, ManualStateScheduler scheduler, _) = Build();

        list.Add(1);
        list.Add(2);

        list.Count.Should().Be(0); // deferred

        scheduler.Drain();
        list.Count.Should().Be(2);
        list.Should().Equal(1, 2);
    }

    [Fact]
    public void A_burst_of_adds_coalesces_into_a_single_flush()
    {
        (RamblaList<int> list, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events) = Build();

        for (int i = 0; i < 5; i++)
        {
            list.Add(i);
        }

        scheduler.PostCount.Should().Be(1); // one flush armed for the whole burst
        scheduler.Drain();

        list.Should().Equal(0, 1, 2, 3, 4);
        events.Should().OnlyContain(e => e.Action == NotifyCollectionChangedAction.Add);
        events.Should().HaveCount(5);
    }

    [Fact]
    public void In_place_change_raises_a_single_replace_with_correct_index()
    {
        (RamblaList<int> list, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events) = Build(new[] { 10, 20, 30 });
        events.Clear();

        list.Replace(1, 99);
        scheduler.Drain();

        list.Should().Equal(10, 99, 30);
        events.Should().ContainSingle();
        events[0].Action.Should().Be(NotifyCollectionChangedAction.Replace);
        events[0].NewStartingIndex.Should().Be(1);
        events[0].OldItems![0].Should().Be(20);
        events[0].NewItems![0].Should().Be(99);
    }

    [Fact]
    public void ReplaceSnapshot_appending_only_touches_the_tail()
    {
        (RamblaList<int> list, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events) = Build(new[] { 1, 2, 3 });
        events.Clear();

        list.ReplaceSnapshot(new[] { 1, 2, 3, 4, 5 });
        scheduler.Drain();

        list.Should().Equal(1, 2, 3, 4, 5);
        events.Should().HaveCount(2);
        events.Should().OnlyContain(e => e.Action == NotifyCollectionChangedAction.Add);
        events[0].NewStartingIndex.Should().Be(3);
        events[1].NewStartingIndex.Should().Be(4);
    }

    [Fact]
    public void ReplaceSnapshot_truncating_only_removes_the_tail()
    {
        (RamblaList<int> list, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events) = Build(new[] { 1, 2, 3, 4, 5 });
        events.Clear();

        list.ReplaceSnapshot(new[] { 1, 2, 3 });
        scheduler.Drain();

        list.Should().Equal(1, 2, 3);
        events.Should().HaveCount(2);
        events.Should().OnlyContain(e => e.Action == NotifyCollectionChangedAction.Remove);
        // Both removals happen at index 3 (the list shrinks under the cursor).
        events.Should().OnlyContain(e => e.OldStartingIndex == 3);
    }

    [Fact]
    public void ReplaceSnapshot_middle_change_is_minimal()
    {
        (RamblaList<int> list, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events) = Build(new[] { 1, 2, 3, 4 });
        events.Clear();

        list.ReplaceSnapshot(new[] { 1, 9, 3, 4 }); // only index 1 differs
        scheduler.Drain();

        list.Should().Equal(1, 9, 3, 4);
        events.Should().ContainSingle().Which.Action.Should().Be(NotifyCollectionChangedAction.Replace);
    }

    [Fact]
    public void A_large_rewrite_collapses_to_a_single_reset()
    {
        (RamblaList<int> list, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events) = Build();
        list.ResetThreshold = 8;
        scheduler.Drain();
        events.Clear();

        list.ReplaceSnapshot(Enumerable.Range(0, 50)); // 50 changes > threshold
        scheduler.Drain();

        list.Should().HaveCount(50);
        events.Should().ContainSingle().Which.Action.Should().Be(NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public void Add_then_remove_in_one_batch_coalesces_to_nothing()
    {
        (RamblaList<int> list, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events) = Build(new[] { 1, 2 });
        events.Clear();

        list.Batch(() =>
        {
            list.Add(3);
            list.Remove(3);
        });
        scheduler.Drain();

        list.Should().Equal(1, 2);
        events.Should().BeEmpty(); // net change is zero
    }

    [Fact]
    public void Count_and_indexer_property_changes_are_raised()
    {
        (RamblaList<int> list, ManualStateScheduler scheduler, _) = Build();
        var props = new List<string>();
        ((INotifyPropertyChanged)list).PropertyChanged += (_, e) => props.Add(e.PropertyName!);

        list.Add(1);
        scheduler.Drain();

        props.Should().Contain("Count").And.Contain("Item[]");
    }

    [Fact]
    public void Exposes_a_read_only_IList_for_wpf_virtualization()
    {
        (RamblaList<int> list, ManualStateScheduler scheduler, _) = Build(new[] { 5, 6, 7 });
        scheduler.Drain();

        var asList = (IList)list;
        asList.IsReadOnly.Should().BeTrue();
        asList.Count.Should().Be(3);
        asList[1].Should().Be(6);
        asList.IndexOf(7).Should().Be(2);
        ((Action)(() => asList.Add(8))).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Concurrent_writers_all_land_in_the_final_state()
    {
        (RamblaList<int> list, ManualStateScheduler scheduler, _) = Build();
        const int perThread = 500;
        const int threads = 4;

        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < perThread; i++)
            {
                list.Add((t * perThread) + i);
            }
        });

        scheduler.Drain();

        list.Count.Should().Be(threads * perThread);
        list.Distinct().Should().HaveCount(threads * perThread); // no lost or duplicated writes
    }

    [Fact]
    public void Concurrent_writers_under_an_inline_scheduler_do_not_corrupt_the_view()
    {
        // ImmediateStateScheduler runs the flush inline on the writing thread, so
        // this exercises concurrent ApplyDiff over the shared _view — which must be
        // serialized. Without the single-flusher guard this throws or loses writes.
        var list = new RamblaList<int>(ImmediateStateScheduler.Instance);
        const int perThread = 1_000;
        const int threads = 4;

        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < perThread; i++)
            {
                list.Add((t * perThread) + i);
            }
        });

        list.Count.Should().Be(threads * perThread);
        list.Distinct().Should().HaveCount(threads * perThread);
    }

    [Fact]
    public void A_reentrant_mutation_from_a_handler_does_not_corrupt_the_view()
    {
        // Under an inline scheduler, a handler that mutates the list re-enters Flush
        // synchronously. The reentrant flush must not run a second ApplyDiff over
        // the _view mid-transition; the change is drained by the running flush.
        var list = new RamblaList<int>(ImmediateStateScheduler.Instance);
        bool injected = false;
        list.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && !injected)
            {
                injected = true;
                list.Add(999); // reentrant write from within the notification
            }
        };

        list.Add(1);

        list.Should().Equal(1, 999); // both applied, in order, no exception
    }
}
