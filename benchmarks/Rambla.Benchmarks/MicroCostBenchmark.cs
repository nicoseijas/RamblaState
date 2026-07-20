using BenchmarkDotNet.Attributes;

namespace Rambla.Benchmarks;

/// <summary>
/// Layer 1 — the honest microbenchmark. Isolates the per-write overhead Rambla
/// adds (dirty tracking, flag arming, batch scope) with the flush dropped, so
/// only the write path is measured. These numbers are NOT the argument for the
/// library — the win is downstream work avoided (see
/// <see cref="NotificationThroughputBenchmark"/>). This exists so we can say "the
/// write path is competitive" without overclaiming, and it is a valid, useful
/// loss: Rambla is slower per raw setter but allocates far less.
/// </summary>
[MemoryDiagnoser]
public class MicroCostBenchmark
{
    private readonly NaiveRow _naive = new();
    private readonly BenchRow _rambla = new(DropScheduler.Instance);
    private int _value;

    [Benchmark(Baseline = true)]
    public void NaiveSetter() => _naive.Bid = _value++;

    [Benchmark]
    public void SetField_Changed() => _rambla.Bid = _value++;

    [Benchmark]
    public void SetField_NoOp() => _rambla.Bid = 7m; // unchanged after warmup: exercises the equality fast-path

    [Benchmark]
    public void BeginUpdate_Batch_Of_Five()
    {
        using (_rambla.BeginUpdate())
        {
            _rambla.Bid = _value++;
            _rambla.Ask = _value++;
            _rambla.Last = _value++;
            _rambla.Volume = _value++;
            _rambla.Pnl = _value++;
        }
    }
}
