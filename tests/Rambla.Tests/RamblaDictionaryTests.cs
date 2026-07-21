using System.Collections.Specialized;
using FluentAssertions;
using Rambla.Scheduling;
using Xunit;

namespace Rambla.Tests;

public sealed class RamblaDictionaryTests
{
    private static (RamblaDictionary<string, int> dict, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events)
        Build(IEnumerable<KeyValuePair<string, int>>? initial = null)
    {
        var scheduler = new ManualStateScheduler();
        var dict = new RamblaDictionary<string, int>(scheduler, initial);
        var events = new List<NotifyCollectionChangedEventArgs>();
        dict.CollectionChanged += (_, e) => events.Add(e);
        return (dict, scheduler, events);
    }

    private static IEnumerable<KeyValuePair<string, int>> Entries(params (string Key, int Value)[] entries)
    {
        foreach ((string key, int value) in entries)
        {
            yield return new KeyValuePair<string, int>(key, value);
        }
    }

    [Fact]
    public void Writes_are_not_visible_until_the_flush()
    {
        (RamblaDictionary<string, int> dict, ManualStateScheduler scheduler, _) = Build();

        dict["a"] = 1;
        dict["b"] = 2;

        dict.Count.Should().Be(0); // deferred
        dict.ContainsKey("a").Should().BeFalse();

        scheduler.Drain();

        dict.Count.Should().Be(2);
        dict["a"].Should().Be(1);
        dict["b"].Should().Be(2);
    }

    [Fact]
    public void A_burst_of_updates_to_one_key_coalesces_into_a_single_replace()
    {
        (RamblaDictionary<string, int> dict, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events) =
            Build(Entries(("a", 1)));
        events.Clear();

        dict["a"] = 2;
        dict["a"] = 3;
        dict["a"] = 4;

        scheduler.PostCount.Should().Be(1); // one flush armed for the whole burst
        scheduler.Drain();

        dict["a"].Should().Be(4); // latest value wins
        events.Should().ContainSingle().Which.Action.Should().Be(NotifyCollectionChangedAction.Replace);
    }

    [Fact]
    public void New_keys_append_in_insertion_order()
    {
        (RamblaDictionary<string, int> dict, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events) =
            Build(Entries(("a", 1)));
        events.Clear();

        dict["b"] = 2;
        dict["c"] = 3;
        scheduler.Drain();

        dict.Select(e => e.Key).Should().Equal("a", "b", "c");
        events.Should().HaveCount(2);
        events.Should().OnlyContain(e => e.Action == NotifyCollectionChangedAction.Add);
        events[0].NewStartingIndex.Should().Be(1);
        events[1].NewStartingIndex.Should().Be(2);
    }

    [Fact]
    public void Updating_a_key_preserves_its_position()
    {
        (RamblaDictionary<string, int> dict, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events) =
            Build(Entries(("a", 1), ("b", 2), ("c", 3)));
        events.Clear();

        dict["b"] = 99;
        scheduler.Drain();

        dict.Select(e => e.Key).Should().Equal("a", "b", "c");
        events.Should().ContainSingle();
        events[0].Action.Should().Be(NotifyCollectionChangedAction.Replace);
        events[0].NewStartingIndex.Should().Be(1);
        ((KeyValuePair<string, int>)events[0].NewItems![0]!).Value.Should().Be(99);
        ((KeyValuePair<string, int>)events[0].OldItems![0]!).Value.Should().Be(2);
    }

    [Fact]
    public void Remove_raises_a_single_remove_at_the_correct_index()
    {
        (RamblaDictionary<string, int> dict, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events) =
            Build(Entries(("a", 1), ("b", 2), ("c", 3)));
        events.Clear();

        dict.Remove("b").Should().BeTrue();
        scheduler.Drain();

        dict.Select(e => e.Key).Should().Equal("a", "c");
        dict.ContainsKey("b").Should().BeFalse();
        events.Should().ContainSingle();
        events[0].Action.Should().Be(NotifyCollectionChangedAction.Remove);
        events[0].OldStartingIndex.Should().Be(1);
    }

    [Fact]
    public void Remove_of_an_absent_key_returns_false_and_schedules_nothing()
    {
        (RamblaDictionary<string, int> dict, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events) =
            Build(Entries(("a", 1)));
        events.Clear();
        int posted = scheduler.PostCount;

        dict.Remove("zzz").Should().BeFalse();

        scheduler.PostCount.Should().Be(posted);
        scheduler.Drain();
        events.Should().BeEmpty();
    }

    [Fact]
    public void Add_throws_on_a_key_already_pending_and_TryAdd_returns_false()
    {
        (RamblaDictionary<string, int> dict, ManualStateScheduler scheduler, _) = Build();

        dict.Add("a", 1); // pending, not yet flushed

        Action act = () => dict.Add("a", 2);
        act.Should().Throw<ArgumentException>();
        dict.TryAdd("a", 3).Should().BeFalse();

        scheduler.Drain();
        dict["a"].Should().Be(1);
    }

    [Fact]
    public void A_net_zero_batch_raises_nothing()
    {
        (RamblaDictionary<string, int> dict, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events) =
            Build(Entries(("a", 1)));
        events.Clear();

        dict.Batch(() =>
        {
            dict["x"] = 10;
            dict.Remove("x");
        });
        scheduler.Drain();

        events.Should().BeEmpty();
        dict.Count.Should().Be(1);
    }

    [Fact]
    public void A_large_rewrite_collapses_to_a_single_reset()
    {
        (RamblaDictionary<string, int> dict, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events) = Build();

        dict.Batch(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                dict[$"k{i}"] = i;
            }
        });
        scheduler.Drain();

        dict.Count.Should().Be(100);
        dict["k42"].Should().Be(42);
        events.Should().ContainSingle().Which.Action.Should().Be(NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public void ReplaceSnapshot_diffs_minimally()
    {
        (RamblaDictionary<string, int> dict, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events) =
            Build(Entries(("a", 1), ("b", 2), ("c", 3)));
        events.Clear();

        dict.ReplaceSnapshot(Entries(("a", 1), ("b", 9), ("c", 3))); // only b changed
        scheduler.Drain();

        dict["b"].Should().Be(9);
        events.Should().ContainSingle().Which.Action.Should().Be(NotifyCollectionChangedAction.Replace);
    }

    [Fact]
    public void ReplaceSnapshot_that_throws_mid_enumeration_has_no_effect()
    {
        (RamblaDictionary<string, int> dict, ManualStateScheduler scheduler, _) =
            Build(Entries(("a", 100), ("b", 200)));

        static IEnumerable<KeyValuePair<string, int>> Faulty()
        {
            yield return new KeyValuePair<string, int>("x", 1);
            yield return new KeyValuePair<string, int>("y", 2);
            throw new InvalidOperationException("boom");
        }

        Action act = () => dict.ReplaceSnapshot(Faulty());
        act.Should().Throw<InvalidOperationException>();

        dict["c"] = 300; // unrelated later write must not commit any partial snapshot
        scheduler.Drain();

        dict.Select(e => e.Key).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void ReplaceSnapshot_with_duplicate_keys_throws_and_has_no_effect()
    {
        (RamblaDictionary<string, int> dict, ManualStateScheduler scheduler, _) =
            Build(Entries(("a", 1)));

        Action act = () => dict.ReplaceSnapshot(Entries(("x", 1), ("x", 2)));
        act.Should().Throw<ArgumentException>();

        scheduler.Drain();
        dict.Select(e => e.Key).Should().Equal("a");
    }

    [Fact]
    public void Flush_armed_before_a_batch_does_not_run_while_the_batch_is_open()
    {
        (RamblaDictionary<string, int> dict, ManualStateScheduler scheduler, List<NotifyCollectionChangedEventArgs> events) = Build();

        dict["a"] = 1; // arms a flush
        IDisposable batch = dict.BeginUpdate();
        dict["b"] = 2;

        scheduler.Drain(); // the pre-batch flush is dispatched mid-batch
        events.Should().BeEmpty(); // ...and must defer entirely

        batch.Dispose();
        scheduler.Drain();

        dict.Count.Should().Be(2); // both delivered together after the batch closes
        dict["a"].Should().Be(1);
        dict["b"].Should().Be(2);
    }

    [Fact]
    public void Re_adding_a_removed_key_appends_at_the_end()
    {
        (RamblaDictionary<string, int> dict, ManualStateScheduler scheduler, _) =
            Build(Entries(("a", 1), ("b", 2), ("c", 3)));

        dict.Batch(() =>
        {
            dict.Remove("a");
            dict["a"] = 9;
        });
        scheduler.Drain();

        dict.Select(e => e.Key).Should().Equal("b", "c", "a");
        dict["a"].Should().Be(9);
    }

    [Fact]
    public void Keyed_and_ordered_reads_stay_consistent_after_every_flush()
    {
        (RamblaDictionary<string, int> dict, ManualStateScheduler scheduler, _) = Build();

        dict["a"] = 1;
        dict["b"] = 2;
        scheduler.Drain();
        dict.Remove("a");
        dict["c"] = 3;
        scheduler.Drain();

        dict.Select(e => e.Key).Should().Equal("b", "c");
        dict.TryGetValue("b", out int b).Should().BeTrue();
        b.Should().Be(2);
        dict.TryGetValue("a", out _).Should().BeFalse();
        dict.Keys.Should().Equal("b", "c");
        dict.Values.Should().Equal(2, 3);
    }

    [Fact]
    public void Concurrent_writers_to_distinct_keys_all_land_in_the_final_state()
    {
        var scheduler = new ManualStateScheduler();
        var dict = new RamblaDictionary<int, int>(scheduler);

        Parallel.For(0, 1000, i => dict[i] = i * 10);
        scheduler.Drain();

        dict.Count.Should().Be(1000);
        for (int i = 0; i < 1000; i++)
        {
            dict[i].Should().Be(i * 10);
        }
    }

    [Fact]
    public void Initial_contents_are_visible_immediately_and_duplicate_initial_keys_throw()
    {
        (RamblaDictionary<string, int> dict, _, _) = Build(Entries(("a", 1), ("b", 2)));

        dict.Count.Should().Be(2); // no flush needed
        dict["a"].Should().Be(1);

        Action act = () => Build(Entries(("dup", 1), ("dup", 2)));
        act.Should().Throw<ArgumentException>();
    }
}
