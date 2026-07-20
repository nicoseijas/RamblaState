using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Rambla;
using Rambla.Scheduling;

namespace Rambla.Benchmarks;

/// <summary>
/// The flagship comparison: not "how fast is a single setter" but "how much UI
/// notification work is avoided under sustained high-frequency load". Each run
/// applies <see cref="Updates"/> producer writes; we measure the naive path (one
/// notification per write) against Rambla coalescing a burst into one
/// notification per flush window.
/// </summary>
/// <remarks>
/// <see cref="NotificationCost"/> simulates the real downstream cost each
/// <c>PropertyChanged</c> triggers in a live UI (binding resolution, layout,
/// render). At <c>0</c> — a no-op subscriber — the naive path wins on raw CPU,
/// because there is no downstream work to avoid and Rambla only adds dirty
/// tracking; that case is the honest caveat, not the pitch. As the per-
/// notification cost grows toward anything a real binding pays, the ~1000x fewer
/// notifications dominate and Rambla wins decisively — while allocating a tiny
/// fraction of the naive path's per-event <c>PropertyChangedEventArgs</c> garbage.
/// </remarks>
[MemoryDiagnoser]
public class NotificationThroughputBenchmark
{
    [Params(100_000)]
    public int Updates { get; set; }

    /// <summary>Producer writes per UI flush window (~16 ms at 60 Hz vs a fast feed).</summary>
    [Params(1_000)]
    public int WritesPerFlush { get; set; }

    /// <summary>Simulated downstream work per delivered notification, in spin iterations.</summary>
    [Params(0, 500)]
    public int NotificationCost { get; set; }

    private int _sink;

    [Benchmark(Baseline = true)]
    public int Naive()
    {
        NaiveViewModel vm = new();
        vm.PropertyChanged += OnChanged;
        for (int i = 0; i < Updates; i++)
        {
            vm.Bid = i;
        }

        vm.PropertyChanged -= OnChanged;
        return _sink;
    }

    [Benchmark]
    public int RamblaCoalesced()
    {
        IntervalScheduler scheduler = new();
        BenchState vm = new(scheduler);
        ((INotifyPropertyChanged)vm).PropertyChanged += OnChanged;

        for (int i = 0; i < Updates; i++)
        {
            vm.Bid = i;
            if (i % WritesPerFlush == 0)
            {
                scheduler.Tick();
            }
        }

        scheduler.Tick();
        ((INotifyPropertyChanged)vm).PropertyChanged -= OnChanged;
        return _sink;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        _sink++;
        if (NotificationCost > 0)
        {
            Thread.SpinWait(NotificationCost); // stand-in for binding + layout + render
        }
    }

    private sealed class NaiveViewModel : INotifyPropertyChanged
    {
        private decimal _bid;

        public event PropertyChangedEventHandler? PropertyChanged;

        public decimal Bid
        {
            get => _bid;
            set
            {
                _bid = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bid)));
            }
        }
    }

    private sealed class BenchState : RamblaState
    {
        private decimal _bid;

        public BenchState(IStateScheduler scheduler)
            : base(scheduler)
        {
        }

        public decimal Bid
        {
            get => _bid;
            set => SetField(ref _bid, value);
        }
    }

    /// <summary>Holds at most one pending flush and runs it when <see cref="Tick"/> is called.</summary>
    private sealed class IntervalScheduler : IStateScheduler
    {
        private Action? _pending;

        public void Post(Action flush) => _pending = flush;

        public void Tick()
        {
            Action? flush = _pending;
            _pending = null;
            flush?.Invoke();
        }
    }
}
