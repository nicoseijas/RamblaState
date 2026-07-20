using FluentAssertions;
using Xunit;

namespace Rambla.Tests.Diagnostics;

/// <summary>
/// Tests the core <see cref="IStateProbe"/> hook directly (independent of the
/// diagnostics package), including that it is a pure observer.
/// </summary>
public sealed class StateProbeTests
{
    private sealed class RecordingProbe : IStateProbe
    {
        public List<string> Mutations { get; } = new();
        public int Flushes { get; private set; }
        public List<string> LastFlush { get; } = new();

        public void OnMutation(string propertyName) => Mutations.Add(propertyName);

        public void OnFlush(IReadOnlyList<string> notifiedProperties, TimeSpan raiseDuration)
        {
            Flushes++;
            LastFlush.Clear();
            LastFlush.AddRange(notifiedProperties);
        }
    }

    [Fact]
    public void Probe_sees_each_real_mutation_but_not_no_op_writes()
    {
        var scheduler = new ManualStateScheduler();
        var vm = new DiagTestState(scheduler);
        var probe = new RecordingProbe();
        using IDisposable _ = vm.AttachProbe(probe);

        vm.Bid = 1m;
        vm.Bid = 1m; // no-op: equal value, not a mutation
        vm.Bid = 2m;

        probe.Mutations.Should().Equal(nameof(DiagTestState.Bid), nameof(DiagTestState.Bid));
    }

    [Fact]
    public void Probe_sees_the_coalesced_flush_even_without_a_property_changed_subscriber()
    {
        var scheduler = new ManualStateScheduler();
        var vm = new DiagTestState(scheduler);
        var probe = new RecordingProbe();
        using IDisposable _ = vm.AttachProbe(probe);

        vm.Bid = 1m;
        vm.Ask = 2m;
        scheduler.Drain();

        probe.Flushes.Should().Be(1);
        probe.LastFlush.Should().BeEquivalentTo(new[] { nameof(DiagTestState.Bid), nameof(DiagTestState.Ask) });
    }

    [Fact]
    public void Detaching_the_probe_stops_callbacks()
    {
        var scheduler = new ManualStateScheduler();
        var vm = new DiagTestState(scheduler);
        var probe = new RecordingProbe();
        IDisposable token = vm.AttachProbe(probe);

        token.Dispose();

        vm.Bid = 1m;
        scheduler.Drain();

        probe.Mutations.Should().BeEmpty();
        probe.Flushes.Should().Be(0);
    }

    [Fact]
    public void AttachProbe_rejects_null()
    {
        var vm = new DiagTestState(new ManualStateScheduler());
        Action act = () => vm.AttachProbe(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
