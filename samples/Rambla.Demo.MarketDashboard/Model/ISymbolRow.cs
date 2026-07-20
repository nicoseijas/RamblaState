using System.ComponentModel;

namespace Rambla.Demo.MarketDashboard.Model;

/// <summary>
/// A single quote row bound to the grid. <see cref="Apply"/> receives one quote
/// (five fields) at once so each implementation can decide how many UI
/// notifications that costs: the naive one emits five, the Rambla one batches
/// into a single coherent flush.
/// </summary>
public interface ISymbolRow : INotifyPropertyChanged
{
    string Symbol { get; }

    decimal Bid { get; }

    decimal Ask { get; }

    decimal Last { get; }

    decimal Volume { get; }

    decimal Pnl { get; }

    void Apply(decimal bid, decimal ask, decimal last, decimal volume, decimal pnl);
}
