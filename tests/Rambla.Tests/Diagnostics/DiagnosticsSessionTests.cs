using System.ComponentModel;
using FluentAssertions;
using Rambla.Diagnostics;
using Xunit;

namespace Rambla.Tests.Diagnostics;

public sealed class DiagnosticsSessionTests
{
    [Fact]
    public void Snapshot_reports_coalescing_of_repeated_writes()
    {
        var scheduler = new ManualStateScheduler();
        var clock = new FakeClock();
        var vm = new DiagTestState(scheduler);
        vm.PropertyChanged += (_, __) => { }; // a bound UI: notifications are raised
        using DiagnosticsSession session = StateDiagnostics.Attach(vm, clock: clock);

        vm.Bid = 1m;
        vm.Bid = 2m;
        vm.Bid = 3m;      // three mutations to one property...
        scheduler.Drain(); // ...coalesced into a single flush/notification

        clock.Advance(TimeSpan.FromSeconds(1));
        DiagnosticsSnapshot s = session.Snapshot();

        s.TotalMutations.Should().Be(3);
        s.TotalNotifications.Should().Be(1);
        s.MutationsPerSecond.Should().Be(3d);
        s.NotificationsPerSecond.Should().Be(1d);
        s.CoalescingRatio.Should().BeApproximately(2d / 3d, 1e-9);
    }

    [Fact]
    public void Rates_are_computed_over_the_elapsed_window()
    {
        var scheduler = new ManualStateScheduler();
        var clock = new FakeClock();
        var vm = new DiagTestState(scheduler);
        vm.PropertyChanged += (_, __) => { }; // a bound UI: notifications are raised
        using DiagnosticsSession session = StateDiagnostics.Attach(vm, clock: clock);

        for (int i = 0; i < 100; i++)
        {
            vm.Bid = i + 1; // avoid writing the default 0 (which is a no-op, not a mutation)
            scheduler.Drain();
        }

        clock.Advance(TimeSpan.FromSeconds(2));
        DiagnosticsSnapshot s = session.Snapshot();

        // 100 mutations / 100 notifications over a 2-second window.
        s.MutationsPerSecond.Should().Be(50d);
        s.NotificationsPerSecond.Should().Be(50d);
        s.FlushesPerSecond.Should().Be(50d);
    }

    [Fact]
    public void Snapshot_window_deltas_reset_between_reads()
    {
        var scheduler = new ManualStateScheduler();
        var clock = new FakeClock();
        var vm = new DiagTestState(scheduler);
        using DiagnosticsSession session = StateDiagnostics.Attach(vm, clock: clock);

        vm.Bid = 1m;
        scheduler.Drain();
        clock.Advance(TimeSpan.FromSeconds(1));
        session.Snapshot(); // consume the first window

        clock.Advance(TimeSpan.FromSeconds(1)); // a second, empty window
        DiagnosticsSnapshot s = session.Snapshot();

        s.MutationsPerSecond.Should().Be(0d);
        s.TotalMutations.Should().Be(1); // cumulative total is retained
    }

    [Fact]
    public void Hot_properties_are_ranked_by_notification_rate()
    {
        var scheduler = new ManualStateScheduler();
        var clock = new FakeClock();
        var vm = new DiagTestState(scheduler);
        vm.PropertyChanged += (_, __) => { }; // a bound UI: notifications are raised
        using DiagnosticsSession session = StateDiagnostics.Attach(vm, clock: clock);

        // Ask flushes on every write; Bid's writes mostly coalesce.
        vm.Bid = 1m;
        vm.Bid = 2m;
        vm.Bid = 3m;
        scheduler.Drain(); // Bid: 1 notification
        for (int i = 0; i < 5; i++)
        {
            vm.Ask = i + 1;
            scheduler.Drain(); // Ask: 5 notifications
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        DiagnosticsSnapshot s = session.Snapshot();

        s.HotProperties.Should().HaveCount(2);
        s.HotProperties[0].Name.Should().Be(nameof(DiagTestState.Ask));
        s.HotProperties[0].NotificationsPerSecond.Should().Be(5d);
        s.HotProperties[1].Name.Should().Be(nameof(DiagTestState.Bid));
        s.HotProperties[1].CoalescingRatio.Should().BeApproximately(2d / 3d, 1e-9);
    }

    [Fact]
    public void A_property_above_the_threshold_produces_a_batch_recommendation()
    {
        var scheduler = new ManualStateScheduler();
        var clock = new FakeClock();
        var vm = new DiagTestState(scheduler);
        vm.PropertyChanged += (_, __) => { }; // a bound UI: notifications are raised
        var options = new DiagnosticsOptions { HotNotificationsPerSecond = 10d };
        using DiagnosticsSession session = StateDiagnostics.Attach(vm, clock: clock, options: options);

        for (int i = 0; i < 20; i++)
        {
            vm.Ask = i + 1;
            scheduler.Drain(); // 20 notifications in a 1-second window => 20/sec > 10
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        DiagnosticsSnapshot s = session.Snapshot();

        s.Recommendations.Should().ContainSingle()
            .Which.Message.Should().Contain("Ask").And.Contain("BeginUpdate");
        s.Recommendations[0].Severity.Should().Be(RecommendationSeverity.Warning);
    }

    [Fact]
    public void Without_a_subscriber_no_notifications_are_reported_but_coalescing_still_is()
    {
        var scheduler = new ManualStateScheduler();
        var clock = new FakeClock();
        var vm = new DiagTestState(scheduler); // nothing bound: no PropertyChanged subscriber
        using DiagnosticsSession session = StateDiagnostics.Attach(vm, clock: clock);

        vm.Bid = 1m;
        vm.Bid = 2m;
        vm.Bid = 3m;
        scheduler.Drain();

        clock.Advance(TimeSpan.FromSeconds(1));
        DiagnosticsSnapshot s = session.Snapshot();

        // No phantom UI traffic...
        s.TotalNotifications.Should().Be(0);
        s.NotificationsPerSecond.Should().Be(0d);
        s.Recommendations.Should().BeEmpty();

        // ...but the engine's own activity is still measured: three mutations
        // coalesced into one flushed property.
        s.TotalMutations.Should().Be(3);
        s.CoalescingRatio.Should().BeApproximately(2d / 3d, 1e-9);
        s.HotProperties.Should().ContainSingle();
        s.HotProperties[0].Name.Should().Be(nameof(DiagTestState.Bid));
        s.HotProperties[0].MutationsPerSecond.Should().Be(3d);
        s.HotProperties[0].NotificationsPerSecond.Should().Be(0d);
    }

    [Fact]
    public void Disposing_the_session_detaches_the_probe()
    {
        var scheduler = new ManualStateScheduler();
        var clock = new FakeClock();
        var vm = new DiagTestState(scheduler);
        DiagnosticsSession session = StateDiagnostics.Attach(vm, clock: clock);

        session.Dispose();

        vm.Bid = 1m;
        scheduler.Drain();
        clock.Advance(TimeSpan.FromSeconds(1));
        DiagnosticsSnapshot s = session.Snapshot();

        s.TotalMutations.Should().Be(0); // nothing observed after detach
    }

    [Fact]
    public void Attaching_a_probe_does_not_change_notification_behaviour()
    {
        var scheduler = new ManualStateScheduler();
        var vm = new DiagTestState(scheduler);
        var notified = new List<string>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => notified.Add(e.PropertyName!);

        using DiagnosticsSession session = StateDiagnostics.Attach(vm);

        vm.Bid = 1m;
        vm.Ask = 2m;
        scheduler.Drain();

        notified.Should().BeEquivalentTo(new[] { nameof(DiagTestState.Bid), nameof(DiagTestState.Ask) });
    }

    [Fact]
    public void Report_renders_the_headline_lines()
    {
        var scheduler = new ManualStateScheduler();
        var clock = new FakeClock();
        var vm = new DiagTestState(scheduler);
        using DiagnosticsSession session = StateDiagnostics.Attach(vm, clock: clock);

        vm.Bid = 1m;
        vm.Bid = 2m;
        scheduler.Drain();
        clock.Advance(TimeSpan.FromSeconds(1));

        string report = session.Snapshot().ToString();

        report.Should().Contain(nameof(DiagTestState));
        report.Should().Contain("Incoming state mutations");
        report.Should().Contain("Coalescing");
        report.Should().Contain("UI thread budget");
    }
}
