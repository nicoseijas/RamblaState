using FluentAssertions;
using Rambla.Scheduling;
using Xunit;

namespace Rambla.Tests;

/// <summary>
/// Regression tests for the two fault paths that used to leave the pipeline in a
/// bad state: an <see cref="IStateScheduler.Post"/> that throws (which left the
/// scheduled flag armed forever, silently wedging all future flushes), and a
/// collection-change handler that throws mid-diff (which left the visible
/// contents partially applied until an unrelated mutation happened to arrive).
/// </summary>
public sealed class FaultToleranceTests
{
    // ---- IStateScheduler.Post throws: the armed flag must roll back ----

    [Fact]
    public void State_write_survives_a_scheduler_that_rejects_the_post()
    {
        ThrowOnFirstPostScheduler scheduler = new();
        MarketViewModel vm = new(scheduler);
        List<string?> notified = new();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        Action write = () => vm.Bid = 1m;
        write.Should().Throw<InvalidOperationException>().WithMessage("post rejected");

        // The dirty set must survive the failed post: the next write re-arms,
        // re-posts, and the flush delivers BOTH properties.
        vm.Ask = 2m;
        scheduler.Inner.Drain();

        notified.Should().Contain(nameof(MarketViewModel.Bid));
        notified.Should().Contain(nameof(MarketViewModel.Ask));
    }

    [Fact]
    public void Closing_a_batch_survives_a_scheduler_that_rejects_the_post()
    {
        ThrowOnFirstPostScheduler scheduler = new();
        MarketViewModel vm = new(scheduler);
        List<string?> notified = new();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        Action close = () =>
        {
            using (vm.BeginUpdate())
            {
                vm.Bid = 1m;
            }
        };
        close.Should().Throw<InvalidOperationException>().WithMessage("post rejected");

        vm.Ask = 2m;
        scheduler.Inner.Drain();

        notified.Should().Contain(nameof(MarketViewModel.Bid));
        notified.Should().Contain(nameof(MarketViewModel.Ask));
    }

    [Fact]
    public void List_write_survives_a_scheduler_that_rejects_the_post()
    {
        ThrowOnFirstPostScheduler scheduler = new();
        RamblaList<int> list = new(scheduler);

        Action add = () => list.Add(1);
        add.Should().Throw<InvalidOperationException>().WithMessage("post rejected");

        list.Add(2);
        scheduler.Inner.Drain();

        list.Should().Equal(1, 2);
    }

    [Fact]
    public void Dictionary_write_survives_a_scheduler_that_rejects_the_post()
    {
        ThrowOnFirstPostScheduler scheduler = new();
        RamblaDictionary<string, int> dict = new(scheduler);

        Action write = () => dict["a"] = 1;
        write.Should().Throw<InvalidOperationException>().WithMessage("post rejected");

        dict["b"] = 2;
        scheduler.Inner.Drain();

        dict.Count.Should().Be(2);
        dict["a"].Should().Be(1);
        dict["b"].Should().Be(2);
    }

    // ---- CollectionChanged handler throws mid-diff: the aborted transition ----
    // ---- stays pending and a recovery flush reconciles it automatically    ----

    [Fact]
    public void List_recovers_from_a_handler_that_throws_once_without_a_further_mutation()
    {
        ManualStateScheduler scheduler = new();
        RamblaList<int> list = new(scheduler, new[] { 1, 2, 3 });
        bool thrown = false;
        list.CollectionChanged += (_, _) =>
        {
            if (!thrown)
            {
                thrown = true;
                throw new InvalidOperationException("boom");
            }
        };

        list.ReplaceSnapshot(new[] { 10, 20, 30 });
        Action drain = scheduler.Drain;
        drain.Should().Throw<InvalidOperationException>();

        // The aborted flush applied only its first change before the handler threw.
        list.Should().Equal(10, 2, 3);

        // No further mutation: the recovery flush alone must reconcile the rest.
        scheduler.Drain();
        list.Should().Equal(10, 20, 30);
    }

    [Fact]
    public void Dictionary_recovers_from_a_handler_that_throws_once_without_a_further_mutation()
    {
        ManualStateScheduler scheduler = new();
        RamblaDictionary<string, int> dict = new(scheduler, new Dictionary<string, int>
        {
            ["a"] = 1,
            ["b"] = 2,
            ["c"] = 3,
        });
        bool thrown = false;
        dict.CollectionChanged += (_, _) =>
        {
            if (!thrown)
            {
                thrown = true;
                throw new InvalidOperationException("boom");
            }
        };

        dict.ReplaceSnapshot(new Dictionary<string, int>
        {
            ["a"] = 10,
            ["b"] = 20,
            ["c"] = 30,
        });
        Action drain = scheduler.Drain;
        drain.Should().Throw<InvalidOperationException>();

        // No further mutation: the recovery flush alone must reconcile the rest.
        scheduler.Drain();
        dict["a"].Should().Be(10);
        dict["b"].Should().Be(20);
        dict["c"].Should().Be(30);
    }

    [Fact]
    public void An_always_throwing_handler_on_an_inline_scheduler_neither_wedges_nor_recurses()
    {
        // The recovery flush is gated to a single attempt per failure; with an
        // inline scheduler that attempt runs synchronously inside the catch, so
        // a handler that ALWAYS throws must not recurse unboundedly.
        RamblaList<int> list = new(ImmediateStateScheduler.Instance);
        list.CollectionChanged += (_, _) => throw new InvalidOperationException("boom");

        Action add1 = () => list.Add(1);
        add1.Should().Throw<InvalidOperationException>();
        Action add2 = () => list.Add(2);
        add2.Should().Throw<InvalidOperationException>();

        // Each aborted flush had already applied its single change to the view.
        list.Should().Equal(1, 2);
    }

    /// <summary>
    /// Rejects the first post (models a scheduler whose dispatcher refuses work),
    /// then delegates to a <see cref="ManualStateScheduler"/> the test drains.
    /// </summary>
    private sealed class ThrowOnFirstPostScheduler : IStateScheduler
    {
        private int _posts;

        public ManualStateScheduler Inner { get; } = new();

        public void Post(Action flush)
        {
            if (Interlocked.Increment(ref _posts) == 1)
            {
                throw new InvalidOperationException("post rejected");
            }

            Inner.Post(flush);
        }
    }
}
