using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Rambla.Demo.MarketDashboard.Model;

/// <summary>
/// The baseline: a textbook <see cref="INotifyPropertyChanged"/> row that raises
/// one event per field, on the calling (background) thread. WPF marshals each of
/// those to the dispatcher — this is exactly the flood Rambla exists to avoid.
/// </summary>
public sealed class NaiveSymbolRow : ISymbolRow
{
    private decimal _bid;
    private decimal _ask;
    private decimal _last;
    private decimal _volume;
    private decimal _pnl;

    public NaiveSymbolRow(string symbol) => Symbol = symbol;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Symbol { get; }

    public decimal Bid { get => _bid; private set => Set(ref _bid, value); }

    public decimal Ask { get => _ask; private set => Set(ref _ask, value); }

    public decimal Last { get => _last; private set => Set(ref _last, value); }

    public decimal Volume { get => _volume; private set => Set(ref _volume, value); }

    public decimal Pnl { get => _pnl; private set => Set(ref _pnl, value); }

    public void Apply(decimal bid, decimal ask, decimal last, decimal volume, decimal pnl)
    {
        Bid = bid;
        Ask = ask;
        Last = last;
        Volume = volume;
        Pnl = pnl;
    }

    private void Set(ref decimal field, decimal value, [CallerMemberName] string? name = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
