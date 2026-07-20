using Rambla.Scheduling;

namespace Rambla.Demo.MarketDashboard.Model;

/// <summary>
/// The Rambla row, dogfooding the <c>[State]</c> generator: each annotated field
/// becomes an observable property routed through <c>SetField</c>. <see
/// cref="Apply"/> wraps the five writes in a single <c>BeginUpdate</c> batch, so
/// one quote costs one coherent flush.
/// </summary>
public sealed partial class RamblaSymbolRow : RamblaState, ISymbolRow
{
    [State] private decimal _bid;
    [State] private decimal _ask;
    [State] private decimal _last;
    [State] private decimal _volume;
    [State] private decimal _pnl;

    public RamblaSymbolRow(string symbol, IStateScheduler scheduler)
        : base(scheduler)
        => Symbol = symbol;

    public string Symbol { get; }

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
