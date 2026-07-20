using System.ComponentModel;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Rambla;
using Rambla.Scheduling;

namespace Rambla.Benchmarks;

/// <summary>
/// Secondary, honesty-first micro-costs: the per-write overhead Rambla adds
/// (dirty tracking, flag arming, batch scope). These numbers are NOT the
/// argument for the library — the win is downstream work avoided, measured by
/// <see cref="NotificationThroughputBenchmark"/>. This exists so we can say
/// "the write path is competitive" without overclaiming.
/// </summary>
[MemoryDiagnoser]
public class MicroCostBenchmark
{
    private readonly Naive _naive = new();
    private readonly Bench _rambla = new();
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

    private sealed class Naive : INotifyPropertyChanged
    {
        private decimal _bid;

        public event PropertyChangedEventHandler? PropertyChanged;

        public decimal Bid
        {
            get => _bid;
            set
            {
                if (_bid == value)
                {
                    return;
                }

                _bid = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bid)));
            }
        }
    }

    // Drops the flush so we isolate the write/dirty/arm cost, not notification.
    private sealed class DropScheduler : IStateScheduler
    {
        public static readonly DropScheduler Instance = new();

        public void Post(Action flush)
        {
        }
    }

    private sealed class Bench : RamblaState
    {
        private decimal _bid;
        private decimal _ask;
        private decimal _last;
        private decimal _volume;
        private decimal _pnl;

        public Bench()
            : base(DropScheduler.Instance)
        {
        }

        public decimal Bid { get => _bid; set => SetField(ref _bid, value); }

        public decimal Ask { get => _ask; set => SetField(ref _ask, value); }

        public decimal Last { get => _last; set => SetField(ref _last, value); }

        public decimal Volume { get => _volume; set => SetField(ref _volume, value); }

        public decimal Pnl { get => _pnl; set => SetField(ref _pnl, value); }
    }
}
