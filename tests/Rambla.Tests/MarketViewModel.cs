using Rambla.Scheduling;

namespace Rambla.Tests;

/// <summary>
/// Hand-written state used by the tests. Once the <c>[State]</c> generator lands,
/// these properties become generated; the observable contract stays identical.
/// </summary>
internal sealed class MarketViewModel : RamblaState
{
    private decimal _bid;
    private decimal _ask;
    private decimal _pnl;

    public MarketViewModel(IStateScheduler scheduler, bool collectMetrics = false)
        : base(scheduler, collectMetrics)
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

    public decimal Pnl
    {
        get => _pnl;
        set => SetField(ref _pnl, value);
    }
}
