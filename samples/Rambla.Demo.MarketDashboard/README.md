# Rambla — Real-Time Market Dashboard (demo)

The demo that sells the library. It runs one synthetic market feed through three
paths and shows, live, how much UI work each one costs.

```bash
dotnet run -c Release --project samples/Rambla.Demo.MarketDashboard
```

Requires Windows (WPF, `net10.0-windows`).

## What it does

A background feed random-walks quotes (Bid/Ask/Last/Volume/PnL) across N symbols
at a target rate and writes them into a virtualized `DataGrid`. You pick the
path and the load; the top row reports per-second metrics.

### Modes

| Mode                | What it exercises                                                        |
| ------------------- | ------------------------------------------------------------------------ |
| **Naive**           | Textbook `INotifyPropertyChanged` — one event per field, on the feed thread. |
| **Rambla Immediate**| Rambla with the immediate (non-coalescing) scheduler — no magic without a coalescing scheduler. |
| **Rambla Coalesced**| Rambla coalescing flushes to the UI thread at the chosen refresh rate (e.g. 60 Hz). |

### Live counters

- **Incoming mutations/s** — field writes the feed produced.
- **PropertyChanged/s** — notifications actually raised to bindings.
- **Scheduler posts/s** — UI-thread hops requested.
- **Coalescing %** — `1 − notifications/mutations`.
- **Flush p50 / p95 / p99** — time spent in each notification pass.
- **UI lag (max/s)** — how late a background-priority dispatcher callback runs;
  a direct read on dispatcher saturation.

## What to look for

Start with **Naive**, 250 symbols, 50,000 quotes/s. Watch `PropertyChanged/s`
track incoming mutations and UI lag climb. Switch to **Rambla Coalesced** at the
same load: incoming mutations stay high, but `PropertyChanged/s` collapses toward
`~60 × dirty rows`, coalescing climbs past 99%, and UI lag settles.

That contrast — same feed, a fraction of the UI work — is the whole point.

> Numbers depend on your machine. The story (mutations ≫ notifications, stable UI)
> does not.
