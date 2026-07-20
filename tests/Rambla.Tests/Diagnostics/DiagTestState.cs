using Rambla.Scheduling;

namespace Rambla.Tests.Diagnostics;

/// <summary>A minimal hand-written state (no generator) for diagnostics tests.</summary>
internal sealed class DiagTestState : RamblaState
{
    private decimal _bid;
    private decimal _ask;

    public DiagTestState(IStateScheduler scheduler)
        : base(scheduler)
    {
    }

    public decimal Bid
    {
        get => _bid;
        set => SetField(ref _bid, value);
    }

    public decimal Ask
    {
        get => _ask;
        set => SetField(ref _ask, value);
    }
}
