using System.Diagnostics;
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
    public void A_throwing_flush_still_records_its_execution_time()
    {
        var inner = new ManualStateScheduler();
        var scheduler = new DiagnosticsScheduler(inner);

        scheduler.Post(() =>
        {
            // Spin for at least one Stopwatch tick so the recorded duration
            // cannot round to zero, then fail like a fail-fast subscriber.
            long start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetTimestamp() == start)
            {
            }

            throw new InvalidOperationException("boom");
        });

        Action drain = inner.Drain;
        drain.Should().Throw<InvalidOperationException>();

        DispatcherCounters counters = scheduler.ReadCounters();
        counters.Hops.Should().Be(1);
        counters.FlushTicks.Should().BePositive();
        counters.MaxFlushTicks.Should().BePositive();
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
