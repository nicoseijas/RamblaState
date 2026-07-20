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

### The macrobenchmark: producer → visible latency

Micro-benchmarks show fewer notifications; they cannot show whether coalescing
adds *staleness*. This is the number that decides the refresh-rate sweet spot.

The feed stamps a monotonic sequence into a dedicated **canary row** and records
when it produced each value. On every rendered frame (`CompositionTarget.Rendering`)
the UI reads the canary's currently visible value and records how long it took to
become visible. **Latency p50 / p95 / p99** report that end-to-end delay.

- **Naive** keeps latency low *until* the dispatcher saturates, then p99 spikes as
  updates queue behind the flood.
- **Rambla Coalesced** caps notification work but introduces at most ~one refresh
  interval of staleness — so latency should stay bounded and *predictable* even
  as incoming load climbs.

## What to look for

Start with **Naive**, 250 symbols, 50,000 quotes/s. Watch `PropertyChanged/s`
track incoming mutations and UI lag climb. Switch to **Rambla Coalesced** at the
same load: incoming mutations stay high, but `PropertyChanged/s` collapses toward
`~60 × dirty rows`, coalescing climbs past 99%, and UI lag settles.

That contrast — same feed, a fraction of the UI work — is the whole point.

### Finding the refresh-rate frontier

In **Rambla Coalesced**, sweep the **Refresh Hz** field (e.g. 10 → 30 → 60 → 120)
at a fixed load and read the trade-off directly:

- Lower Hz → fewer `PropertyChanged/s` and posts/s, but higher latency p50/p99.
- Higher Hz → lower latency, but more UI work (toward the immediate path).

The knee of that curve is where `MaxRefreshRate` should sit — usually ~60 Hz. The
demo turns that default from an arbitrary constant into an observed one.

> Numbers depend on your machine. The story (mutations ≫ notifications, bounded
> and predictable latency) does not.
