using System.Collections.Generic;
using System.ComponentModel;
using FluentAssertions;
using Rambla.Scheduling;
using Xunit;

namespace Rambla.Tests;

public sealed class RamblaStateTests
{
    private static List<string> TrackChanges(RamblaState state)
    {
        List<string> changed = new();
        ((INotifyPropertyChanged)state).PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        return changed;
    }

    [Fact]
    public void Coalesces_repeated_writes_of_same_property_into_one_notification()
    {
        ManualStateScheduler scheduler = new();
        MarketViewModel vm = new(scheduler);
        List<string> changed = TrackChanges(vm);

        vm.Bid = 1.23m;
        vm.Bid = 1.24m;
        vm.Bid = 1.25m;

        // Nothing is raised until the scheduler flushes.
        changed.Should().BeEmpty();
        scheduler.PostCount.Should().Be(1, "a burst schedules a single flush");

        scheduler.Drain();

        changed.Should().ContainSingle().Which.Should().Be(nameof(MarketViewModel.Bid));
        vm.Bid.Should().Be(1.25m, "latest value wins");
    }

    [Fact]
    public void Distinct_properties_each_notify_once_per_flush()
    {
        ManualStateScheduler scheduler = new();
        MarketViewModel vm = new(scheduler);
        List<string> changed = TrackChanges(vm);

        vm.Bid = 1.23m;
        vm.Ask = 1.24m;
        vm.Pnl = 5m;
        scheduler.Drain();

        changed.Should().BeEquivalentTo(
            new[] { nameof(MarketViewModel.Bid), nameof(MarketViewModel.Ask), nameof(MarketViewModel.Pnl) });
    }

    [Fact]
    public void Unchanged_value_raises_no_notification()
    {
        ManualStateScheduler scheduler = new();
        MarketViewModel vm = new(scheduler) { Bid = 1.23m };
        scheduler.Drain();

        List<string> changed = TrackChanges(vm);
        vm.Bid = 1.23m;
        scheduler.Drain();

        changed.Should().BeEmpty();
    }

    [Fact]
    public void BeginUpdate_defers_flush_until_scope_disposes()
    {
        ManualStateScheduler scheduler = new();
        MarketViewModel vm = new(scheduler);
        List<string> changed = TrackChanges(vm);

        using (vm.BeginUpdate())
        {
            vm.Bid = 1.23m;
            vm.Ask = 1.24m;
            scheduler.PostCount.Should().Be(0, "no flush is scheduled inside a batch");
        }

        scheduler.PostCount.Should().Be(1, "disposing the batch schedules exactly one flush");
        scheduler.Drain();
        changed.Should().BeEquivalentTo(
            new[] { nameof(MarketViewModel.Bid), nameof(MarketViewModel.Ask) });
    }

    [Fact]
    public void Immediate_scheduler_notifies_synchronously()
    {
        MarketViewModel vm = new(ImmediateStateScheduler.Instance);
        List<string> changed = TrackChanges(vm);

        vm.Bid = 1.23m;

        changed.Should().ContainSingle().Which.Should().Be(nameof(MarketViewModel.Bid));
    }
}
