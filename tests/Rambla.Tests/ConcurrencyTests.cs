using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Rambla.Scheduling;
using Xunit;

namespace Rambla.Tests;

/// <summary>
/// Attacks the concurrent core: reentrancy, the flush/dirty/scheduled-flag race,
/// nested and out-of-order batches, misbehaving subscribers, and contention.
/// These must pass before the semantics are frozen and the generator emits
/// hundreds of properties over them.
/// </summary>
public sealed class ConcurrencyTests
{
    [Fact]
    public void Handler_that_mutates_state_during_flush_is_delivered_immediately()
    {
        // Reentrancy under the synchronous scheduler: a subscriber writes another
        // property while the flush is raising notifications. Must not deadlock
        // (notifications are raised outside the lock) and must deliver the write.
        MarketViewModel vm = new(ImmediateStateScheduler.Instance);
        List<string> changed = new();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) =>
        {
            changed.Add(e.PropertyName!);
            if (e.PropertyName == nameof(MarketViewModel.Bid) && vm.Ask != 99m)
            {
                vm.Ask = 99m;
            }
        };

        vm.Bid = 1m;

        vm.Ask.Should().Be(99m);
        changed.Should().Contain(nameof(MarketViewModel.Bid)).And.Contain(nameof(MarketViewModel.Ask));
    }

    [Fact]
    public void Handler_reentrant_mutation_is_delivered_with_deferred_scheduler()
    {
        // The classic race: the flush lowers the scheduled flag and clears the
        // dirty set; a write that lands during notification must re-arm a fresh
        // flush rather than be lost.
        ManualStateScheduler scheduler = new();
        MarketViewModel vm = new(scheduler);
        bool injected = false;
        List<string> changed = new();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) =>
        {
            changed.Add(e.PropertyName!);
            if (e.PropertyName == nameof(MarketViewModel.Bid) && !injected)
            {
                injected = true;
                vm.Ask = 42m; // lands while the Bid flush is running
            }
        };

        vm.Bid = 1m;
        scheduler.Drain(); // runs the Bid flush, then the re-armed Ask flush

        vm.Ask.Should().Be(42m);
        changed.Should().Contain(nameof(MarketViewModel.Ask), "the write during the flush must not be lost");
    }

    [Fact]
    public void Write_landing_after_post_before_flush_is_coalesced_not_lost()
    {
        ManualStateScheduler scheduler = new();
        MarketViewModel vm = new(scheduler);
        List<(string Name, decimal Value)> changes = new();
        vm.PropertyChanged += (_, e) => changes.Add((e.PropertyName!, vm.Bid));

        vm.Bid = 1m;   // arms + posts one flush
        vm.Bid = 2m;   // lands after the post, before the flush runs
        scheduler.PostCount.Should().Be(1);

        scheduler.Drain();

        changes.Should().ContainSingle();
        changes[0].Value.Should().Be(2m, "latest value wins, nothing lost");
    }

    [Fact]
    public void Nested_BeginUpdate_flushes_once_when_the_outermost_scope_disposes()
    {
        ManualStateScheduler scheduler = new();
        MarketViewModel vm = new(scheduler);
        List<string> changed = new();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        using (vm.BeginUpdate())
        {
            vm.Bid = 1m;
            using (vm.BeginUpdate())
            {
                vm.Ask = 2m;
            }

            scheduler.PostCount.Should().Be(0, "the inner scope must not flush");
        }

        scheduler.PostCount.Should().Be(1);
        scheduler.Drain();
        changed.Should().BeEquivalentTo(new[] { nameof(MarketViewModel.Bid), nameof(MarketViewModel.Ask) });
    }

    [Fact]
    public void Batch_scopes_disposed_out_of_order_still_flush_exactly_once()
    {
        ManualStateScheduler scheduler = new();
        MarketViewModel vm = new(scheduler);
        List<string> changed = new();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        IDisposable outer = vm.BeginUpdate();
        IDisposable inner = vm.BeginUpdate();
        vm.Bid = 1m;

        outer.Dispose(); // disposed before inner
        scheduler.PostCount.Should().Be(0, "a batch is still open");
        inner.Dispose();

        scheduler.PostCount.Should().Be(1);
        scheduler.Drain();
        changed.Should().ContainSingle().Which.Should().Be(nameof(MarketViewModel.Bid));
    }

    [Fact]
    public void Double_dispose_of_a_batch_scope_does_not_double_count()
    {
        ManualStateScheduler scheduler = new();
        MarketViewModel vm = new(scheduler);

        IDisposable scope = vm.BeginUpdate();
        vm.Bid = 1m;
        scope.Dispose();
        scope.Dispose(); // no-op

        scheduler.PostCount.Should().Be(1);
    }

    [Fact]
    public void Throwing_subscriber_does_not_wedge_the_state()
    {
        ManualStateScheduler scheduler = new();
        MarketViewModel vm = new(scheduler);
        int good = 0;
        vm.PropertyChanged += (_, __) => throw new InvalidOperationException("boom");
        vm.PropertyChanged += (_, __) => good++;

        vm.Bid = 1m;
        Action drain = scheduler.Drain;
        drain.Should().Throw<InvalidOperationException>();

        // The internal state must not be corrupted: a later write still flushes.
        vm.PropertyChanged += (_, __) => good++;
        vm.Ask = 2m;
        Action drainAgain = scheduler.Drain;
        drainAgain.Should().Throw<InvalidOperationException>();
        vm.Ask.Should().Be(2m);
    }

    [Fact]
    public async Task Concurrent_writers_on_one_property_count_every_mutation_and_never_wedge()
    {
        // Under contention even the immediate scheduler coalesces: while one
        // thread holds a flush armed, another thread's write folds into it. So we
        // do NOT expect notifications == mutations. What must hold: every mutation
        // is counted, no notification exceeds the mutation count, and the pipeline
        // stays live (a post-storm sentinel is delivered, proving nothing wedged).
        MarketViewModel vm = new(ImmediateStateScheduler.Instance, collectMetrics: true);
        int pnlNotifications = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MarketViewModel.Pnl))
            {
                Interlocked.Increment(ref pnlNotifications);
            }
        };

        const int threads = 100;
        const int perThread = 500;
        IEnumerable<Task> writers = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < perThread; i++)
            {
                vm.Pnl = (t * perThread) + i + 1; // globally unique => never a no-op
            }
        }));

        await Task.WhenAll(writers);

        vm.Metrics.Mutations.Should().Be(threads * perThread);
        pnlNotifications.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(threads * perThread);

        int before = pnlNotifications;
        vm.Pnl = (threads * perThread) + 1m;
        pnlNotifications.Should().Be(before + 1, "the pipeline must still deliver after heavy contention");
    }

    [Fact]
    public async Task Concurrent_writers_on_distinct_properties_never_wedge()
    {
        MarketViewModel vm = new(ImmediateStateScheduler.Instance, collectMetrics: true);
        int notifications = 0;
        vm.PropertyChanged += (_, __) => Interlocked.Increment(ref notifications);

        const int threads = 90;
        const int perThread = 400;
        IEnumerable<Task> writers = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < perThread; i++)
            {
                decimal v = (t * perThread) + i + 1;
                switch (t % 3)
                {
                    case 0: vm.Bid = v; break;
                    case 1: vm.Ask = v; break;
                    default: vm.Pnl = v; break;
                }
            }
        }));

        await Task.WhenAll(writers);

        vm.Metrics.Mutations.Should().Be(threads * perThread);
        vm.Metrics.Notifications.Should().BeLessThanOrEqualTo(vm.Metrics.Mutations);

        int before = notifications;
        vm.Bid = (threads * perThread) + 1m;
        notifications.Should().Be(before + 1, "the pipeline must still deliver after heavy contention");
    }

    [Fact]
    public void Repeated_write_then_flush_cycles_never_wedge_the_pipeline()
    {
        // If the flush/flag transition were wrong, a lost flush would leave the
        // scheduled flag stuck true and a later write would never be delivered.
        ManualStateScheduler scheduler = new();
        MarketViewModel vm = new(scheduler);
        decimal? last = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MarketViewModel.Bid))
            {
                last = vm.Bid;
            }
        };

        for (int i = 1; i <= 2_000; i++)
        {
            vm.Bid = i;
            scheduler.Drain();
            last.Should().Be(i, "every cycle's final value must be delivered");
        }
    }
}
