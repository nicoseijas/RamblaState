# Benchmarks

Rambla's thesis is **not** "faster setters." It is "far fewer UI notifications
under sustained load." So we measure two layers, separately and honestly:

1. **Mechanical cost** — what the write path costs (BenchmarkDotNet).
2. **Downstream avoided** — how much UI work coalescing removes, and *where it
   stops helping* (a deterministic ledger).

```bash
# Rigorous time/allocation:
dotnet run -c Release --project benchmarks/Rambla.Benchmarks -- --filter "*" --job short
# Rich ledger — counts, coalescing ratio, break-even, burst shape:
dotnet run -c Release --project benchmarks/Rambla.Benchmarks -- profile
```

All numbers below are indicative (ShortRun, single dev machine, .NET 10).
Reproduce before quoting.

## The headline is architectural, not a multiplier

> **100,000 mutations → ~101 effective notifications — a ~99.9% reduction.**

That reduction is a directly observable property of the coalescing model,
independent of any simulated cost. The *time* multiplier ("31× / 1000× faster")
depends entirely on how expensive each notification is — so we report the
reduction as the headline and the time as a consequence.

## Layer 1 — mechanical write cost (`MicroCostBenchmark`)

Flush dropped, so only the write path is measured. This is a **valid, useful
loss**: Rambla adds synchronization, dirty tracking and scheduling bookkeeping,
so a single setter is slower than a bare `PropertyChanged` — while allocating far
less. Rambla is not a faster setter, and we say so first.

## Layer 2 — notifications under load (`NotificationThroughputBenchmark`)

100,000 writes to one property; naive notifies per write, Rambla coalesces each
1,000-write window into one notification. `Subscriber` models what each delivered
notification triggers downstream.

| Subscriber        | Naive mean | Naive alloc | Rambla mean | Rambla alloc | Ratio (R/N) |
| ----------------- | ---------: | ----------: | ----------: | -----------: | ----------: |
| **No-op**         |    ~599 µs |  2,344 KB   |  ~1,010 µs  |    12.3 KB   | 1.68 — *naive wins* |
| **CPU = 64**      |  ~7,627 µs |  2,344 KB   |  ~1,013 µs  |    12.3 KB   | **0.13** |
| **UI-like ×5**    | ~11,041 µs | 21,871 KB   |  ~1,026 µs  |    32.0 KB   | **0.09** |

Reading it:

- **No-op subscriber → naive wins on raw CPU.** No downstream work to avoid;
  Rambla only adds bookkeeping. The honest caveat, stated up front.
- **Any realistic per-notification cost → Rambla wins big**, because it delivers
  ~101 notifications instead of 100,000. At UI-like fan-out that's ~11× faster
  and **~680× less allocation** (21.4 MB → 32 KB) — the naive path allocates a
  `PropertyChangedEventArgs` per event *plus* per-notification binding garbage.

## Break-even — when does coalescing start paying off?

Sweeping CPU downstream work per notification (`-- profile`):

> **Crossover at ≈ 30 ns of downstream work per notification.** Below that, a bare
> `PropertyChanged` is cheaper; above it, coalescing wins overall.

Since a real WPF binding update (convert → layout invalidation → render) costs far
more than 30 ns, live UIs sit well past the crossover.

## Burst shape — where Rambla stops helping (published on purpose)

Coalescing depends on the **entropy of the update stream**: it only helps when
writes repeat the same properties within a flush window. 100,000 writes spread
across N distinct properties (`-- profile`):

| Distinct props / window | Rambla notifications | Coalescing | Verdict |
| ----------------------: | -------------------: | ---------: | ------- |
| 1                       |                 ~101 |     99.9 % | ideal   |
| 10                      |               ~1,001 |     99.0 % | great   |
| 100                     |              ~10,001 |     90.0 % | strong  |
| 1,000                   |             ~100,000 |      0.0 % | **Rambla loses** |
| 10,000+                 |             ~100,000 |      0.0 % | **Rambla loses** |

When distinct properties per window approach the write count, coalescing → 0% and
Rambla is pure overhead (slower than naive, and allocating more). **This is the
boundary of where Rambla belongs**: high-frequency streams that *repeat* the same
state, not wide fan-outs of one-shot values. See [docs/philosophy.md](./docs/philosophy.md).

## The live picture: the demo

Micro-benchmarks can't show dispatcher saturation or frame latency. The
[market dashboard sample](./samples/Rambla.Demo.MarketDashboard) runs the same
synthetic feed through Naive, Rambla-Immediate and Rambla-Coalesced and shows
incoming mutations/s, `PropertyChanged`/s, coalescing %, flush p50/p95/p99 and UI
lag live. A full end-to-end **producer→visible-value latency** probe (to derive
the refresh-rate sweet spot) is the next macrobenchmark.

## The one-line positioning

> Rambla is not optimized to make individual property mutations faster. It is
> optimized to prevent high-frequency mutations from becoming equally
> high-frequency UI work — when the state repeats faster than the UI can render it.
