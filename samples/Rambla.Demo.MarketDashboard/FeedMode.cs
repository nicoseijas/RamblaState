namespace Rambla.Demo.MarketDashboard;

/// <summary>The three paths the demo compares under identical synthetic load.</summary>
public enum FeedMode
{
    /// <summary>Textbook INotifyPropertyChanged: one event per field, on the feed thread.</summary>
    Naive,

    /// <summary>Rambla routing writes through the immediate (non-coalescing) scheduler.</summary>
    RamblaImmediate,

    /// <summary>Rambla coalescing flushes to the UI thread at a fixed refresh rate.</summary>
    RamblaCoalesced,
}
