using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace Rambla.Tests;

/// <summary>
/// Locks the frozen V1 contracts documented in SEMANTICS.md that aren't already
/// pinned elsewhere: the meaning of "mutation" (§3) and no-op batching (§4).
/// </summary>
public sealed class SemanticsContractTests
{
    [Fact]
    public void Metrics_counts_actual_mutations_not_attempts()
    {
        ManualStateScheduler scheduler = new();
        MarketViewModel vm = new(scheduler, collectMetrics: true);
        List<string> changed = new();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.Bid = 100m; // real change
        vm.Bid = 100m; // no-op
        vm.Bid = 100m; // no-op
        scheduler.Drain();

        vm.Metrics.Mutations.Should().Be(1, "equal values are not mutations");
        changed.Should().ContainSingle();
    }

    [Fact]
    public void Empty_batch_schedules_no_flush()
    {
        ManualStateScheduler scheduler = new();
        MarketViewModel vm = new(scheduler);

        using (vm.BeginUpdate())
        {
            // no writes
        }

        scheduler.PostCount.Should().Be(0);
    }

    [Fact]
    public void Flush_armed_before_a_batch_does_not_run_while_the_batch_is_open()
    {
        ManualStateScheduler scheduler = new();
        MarketViewModel vm = new(scheduler);
        List<string> changed = new();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.Bid = 1m; // arms and posts a flush before the batch opens

        using (vm.BeginUpdate())
        {
            vm.Ask = 2m; // written inside the batch

            // The dispatcher happens to process the pre-batch flush now. It must
            // defer: delivering here would notify Ask mid-batch (broken coherence).
            scheduler.Drain();
            changed.Should().BeEmpty("no notification may be raised while a batch is open");
        }

        scheduler.Drain();
        changed.Should().BeEquivalentTo(
            new[] { nameof(MarketViewModel.Bid), nameof(MarketViewModel.Ask) },
            "closing the batch flushes everything in one coherent pass");
    }

    [Fact]
    public void Batch_with_only_noop_writes_schedules_no_flush()
    {
        ManualStateScheduler scheduler = new();
        MarketViewModel vm = new(scheduler) { Bid = 5m };
        scheduler.Drain();
        int before = scheduler.PostCount;

        using (vm.BeginUpdate())
        {
            vm.Bid = 5m; // unchanged
        }

        scheduler.PostCount.Should().Be(before, "a batch with no real change must not flush");
    }
}
