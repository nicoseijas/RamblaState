using Rambla.Scheduling;

namespace Rambla.Demo.MarketDashboard.Model;

/// <summary>
/// The Rambla row: writes are marked dirty from the feed thread and notified via
/// the injected scheduler. <see cref="Apply"/> wraps the five field writes in a
/// single <c>BeginUpdate</c> batch, so one quote costs one coherent flush.
/// </summary>
public sealed class RamblaSymbolRow : RamblaState, ISymbolRow
{
    private decimal _bid;
    private decimal _ask;
    private decimal _last;
    private decimal _volume;
    private decimal _pnl;

    public RamblaSymbolRow(string symbol, IStateScheduler scheduler)
        : base(scheduler)
        => Symbol = symbol;

    public string Symbol { get; }

    public decimal Bid { get => _bid; private set => SetField(ref _bid, value); }

    public decimal Ask { get => _ask; private set => SetField(ref _ask, value); }

    public decimal Last { get => _last; private set => SetField(ref _last, value); }

    public decimal Volume { get => _volume; private set => SetField(ref _volume, value); }

    public decimal Pnl { get => _pnl; private set => SetField(ref _pnl, value); }

    public void Apply(decimal bid, decimal ask, decimal last, decimal volume, decimal pnl)
    {
        using (BeginUpdate())
        {
            Bid = bid;
            Ask = ask;
            Last = last;
            Volume = volume;
            Pnl = pnl;
        }
    }
}
