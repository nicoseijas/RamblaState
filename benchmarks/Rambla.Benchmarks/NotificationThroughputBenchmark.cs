using System.ComponentModel;
using BenchmarkDotNet.Attributes;

namespace Rambla.Benchmarks;

/// <summary>
/// The flagship comparison: not "how fast is a single setter" but "how much UI
/// notification work is avoided under sustained high-frequency load". 100,000
/// producer writes hit one property; the naive path notifies once per write, the
/// Rambla path coalesces each 1,000-write window into one notification.
/// </summary>
/// <remarks>
/// <see cref="Subscriber"/> models what each delivered notification triggers
/// downstream. With <see cref="SubscriberKind.NoOp"/> the naive path wins on raw
/// CPU — there is no downstream work to avoid, and this is the honest caveat, not
/// the pitch. With CPU or UI-like work (what a real binding pays), delivering
/// ~100 notifications instead of 100,000 dominates and Rambla wins decisively —
/// while allocating a fraction of the naive per-event garbage. See also the
/// deterministic ledger and burst-shape tables from <c>-- profile</c>.
/// </remarks>
[MemoryDiagnoser]
public class NotificationThroughputBenchmark
{
    [Params(100_000)]
    public int Updates { get; set; }

    [Params(1_000)]
    public int WritesPerFlush { get; set; }

    [Params(SubscriberKind.NoOp, SubscriberKind.Cpu, SubscriberKind.UiFanout)]
    public SubscriberKind Subscriber { get; set; }

    /// <summary>Iterations for the CPU subscriber; ignored by the other kinds.</summary>
    [Params(64)]
    public int Work { get; set; }

    private int _sink;

    [Benchmark(Baseline = true)]
    public int Naive()
    {
        var row = new NaiveRow();
        row.PropertyChanged += OnChanged;
        for (int i = 0; i < Updates; i++)
        {
            row.Bid = i + 1;
        }

        row.PropertyChanged -= OnChanged;
        return _sink;
    }

    [Benchmark]
    public int RamblaCoalesced()
    {
        var scheduler = new IntervalScheduler();
        var row = new BenchRow(scheduler);
        row.PropertyChanged += OnChanged;
        for (int i = 0; i < Updates; i++)
        {
            row.Bid = i + 1;
            if (i % WritesPerFlush == 0)
            {
                scheduler.Tick();
            }
        }

        scheduler.Tick();
        row.PropertyChanged -= OnChanged;
        return _sink;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        _sink++;
        decimal value = sender switch
        {
            NaiveRow n => n.Bid,
            BenchRow b => b.Bid,
            _ => 0m,
        };
        Workload.Consume(Subscriber, Work, value);
    }
}
