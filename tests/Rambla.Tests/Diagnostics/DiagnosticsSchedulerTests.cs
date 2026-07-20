using FluentAssertions;
using Rambla.Diagnostics;
using Xunit;

namespace Rambla.Tests.Diagnostics;

public sealed class DiagnosticsSchedulerTests
{
    [Fact]
    public void Scheduler_counts_a_hop_per_dispatched_flush()
    {
        var inner = new ManualStateScheduler();
        var scheduler = new DiagnosticsScheduler(inner);
        var vm = new DiagTestState(scheduler);

        vm.Bid = 1m;
        inner.Drain();
        vm.Bid = 2m;
        inner.Drain();

        scheduler.ReadCounters().Hops.Should().Be(2);
    }

    [Fact]
    public void Snapshot_includes_dispatcher_metrics_when_a_diagnostics_scheduler_is_supplied()
    {
        var inner = new ManualStateScheduler();
        var scheduler = new DiagnosticsScheduler(inner);
        var clock = new FakeClock();
        var vm = new DiagTestState(scheduler);
        using DiagnosticsSession session = StateDiagnostics.Attach(vm, scheduler, clock);

        vm.Bid = 1m;
        inner.Drain();
        vm.Bid = 2m;
        inner.Drain();

        clock.Advance(TimeSpan.FromSeconds(1));
        DiagnosticsSnapshot s = session.Snapshot();

        s.HasDispatcherMetrics.Should().BeTrue();
        s.DispatcherHopsPerSecond.Should().Be(2d);
        s.AverageDispatcherLatency.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        s.ToString().Should().Contain("Dispatcher hops");
    }

    [Fact]
    public void Snapshot_omits_dispatcher_metrics_without_a_diagnostics_scheduler()
    {
        var scheduler = new ManualStateScheduler();
        var clock = new FakeClock();
        var vm = new DiagTestState(scheduler);
        using DiagnosticsSession session = StateDiagnostics.Attach(vm, clock: clock);

        vm.Bid = 1m;
        scheduler.Drain();
        clock.Advance(TimeSpan.FromSeconds(1));

        DiagnosticsSnapshot s = session.Snapshot();

        s.HasDispatcherMetrics.Should().BeFalse();
        s.ToString().Should().NotContain("Dispatcher hops");
    }
}
