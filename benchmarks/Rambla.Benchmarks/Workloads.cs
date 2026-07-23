using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Rambla;
using Rambla.Scheduling;

namespace Rambla.Benchmarks;

/// <summary>How much (and what kind of) work each delivered notification triggers downstream.</summary>
public enum SubscriberKind
{
    /// <summary>Empty handler — the honest worst case for Rambla (no downstream work to avoid).</summary>
    NoOp,

    /// <summary>Allocation-free CPU work: conversion/derived-state math, isolates the compute break-even.</summary>
    Cpu,

    /// <summary>UI-like fan-out: several observers each formatting the value — models binding conversion (allocates).</summary>
    UiFanout,
}

/// <summary>
/// A stand-in for the work a real <c>PropertyChanged</c> provokes. A no-op
/// delegate is the least realistic model of a WPF binding; these three kinds
/// bracket the range from "nothing" to "CPU" to "UI-like fan-out with
/// allocation".
/// </summary>
public static class Workload
{
    // Static sinks defeat dead-code elimination without allocating.
    private static double _numericSink;
    private static string _stringSink = string.Empty;

    public static void Consume(SubscriberKind kind, int work, decimal value)
    {
        switch (kind)
        {
            case SubscriberKind.NoOp:
                return;

            case SubscriberKind.Cpu:
                {
                    double acc = _numericSink;
                    for (int i = 0; i < work; i++)
                    {
                        acc = (acc * 1.0000001) + 1.0;
                    }

                    _numericSink = acc;
                    return;
                }

            case SubscriberKind.UiFanout:
                {
                    // ~5 observers (cells/derived props), each converting the value.
                    for (int i = 0; i < 5; i++)
                    {
                        _stringSink = value.ToString("F2", CultureInfo.InvariantCulture);
                    }

                    return;
                }

            default:
                return;
        }
    }
}

/// <summary>Drops the flush entirely, isolating the write/dirty/arm cost.</summary>
public sealed class DropScheduler : IStateScheduler
{
    public static readonly DropScheduler Instance = new();

    public void Post(Action flush)
    {
    }
}

/// <summary>Holds one pending flush and runs it when <see cref="Tick"/> is called — a deterministic 60 Hz stand-in.</summary>
public sealed class IntervalScheduler : IStateScheduler
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

/// <summary>Textbook single-property INotifyPropertyChanged baseline.</summary>
public sealed class NaiveRow : INotifyPropertyChanged
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

/// <summary>Rambla row with a handful of real properties.</summary>
public sealed class BenchRow : RamblaState
{
    private decimal _bid;
    private decimal _ask;
    private decimal _last;
    private decimal _volume;
    private decimal _pnl;

    public BenchRow(IStateScheduler scheduler)
        : base(scheduler)
    {
    }

    public decimal Bid { get => _bid; set => SetField(ref _bid, value); }

    public decimal Ask { get => _ask; set => SetField(ref _ask, value); }

    public decimal Last { get => _last; set => SetField(ref _last, value); }

    public decimal Volume { get => _volume; set => SetField(ref _volume, value); }

    public decimal Pnl { get => _pnl; set => SetField(ref _pnl, value); }
}

/// <summary>
/// A Rambla state with an arbitrary number of distinct properties, addressed by
/// index. Used to vary the entropy of the update stream: coalescing helps only
/// to the extent that writes repeat the same properties within a flush window.
/// </summary>
public sealed class EntropyState : RamblaState
{
    private readonly decimal[] _values;
    private readonly string[] _names;

    public EntropyState(int propertyCount, IStateScheduler scheduler)
        : base(scheduler)
    {
        _values = new decimal[propertyCount];
        _names = new string[propertyCount];
        for (int i = 0; i < propertyCount; i++)
        {
            _names[i] = "P" + i.ToString(CultureInfo.InvariantCulture);
        }
    }

    public int PropertyCount => _values.Length;

    public void SetAt(int index, decimal value) => SetField(ref _values[index], value, _names[index]);
}

/// <summary>The naive equivalent of <see cref="EntropyState"/>: one event per write.</summary>
public sealed class NaiveEntropyRow : INotifyPropertyChanged
{
    private readonly decimal[] _values;
    private readonly PropertyChangedEventArgs[] _args;

    public NaiveEntropyRow(int propertyCount)
    {
        _values = new decimal[propertyCount];
        _args = new PropertyChangedEventArgs[propertyCount];
        for (int i = 0; i < propertyCount; i++)
        {
            _args[i] = new PropertyChangedEventArgs("P" + i.ToString(CultureInfo.InvariantCulture));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetAt(int index, decimal value)
    {
        if (_values[index] == value)
        {
            return;
        }

        _values[index] = value;
        PropertyChanged?.Invoke(this, _args[index]);
    }
}
