# Benchmarks

Rambla's thesis is **not** "faster setters." It is "far fewer UI notifications
under sustained load." So we measure the axis that matters — notification count,
allocations, and downstream work avoided — not nanoseconds per property setter.

Run them yourself:

```bash
dotnet run -c Release --project benchmarks/Rambla.Benchmarks -- --filter "*"          # full suite
dotnet run -c Release --project benchmarks/Rambla.Benchmarks -- --filter "*Throughput*" --job short
```

## Flagship: notification throughput under load

`NotificationThroughputBenchmark` applies **100,000 producer writes** and
compares the naive path (one `PropertyChanged` per write) against Rambla
coalescing bursts into one notification per flush window (1,000 writes/flush).

`NotificationCost` simulates the real work each notification triggers in a live
UI (binding resolution, layout, render), in spin iterations.

### Indicative results

`ShortRun` (3 iterations), .NET 10, single dev machine — indicative, not a
published claim. Reproduce before quoting.

| NotificationCost        | Path            | Mean         | Allocated | vs Naive        |
| ----------------------- | --------------- | -----------: | --------: | --------------- |
| **0** (no-op subscriber)| Naive           |      ~210 µs | 2,344 KB  | baseline        |
|                         | Rambla coalesced|      ~993 µs |    12 KB  | 4.7× slower CPU, **0.5% alloc** |
| **500** (realistic)     | Naive           | ~2,218,000 µs (**2.2 s**) | 2,344 KB | baseline |
|                         | Rambla coalesced|    ~2,171 µs (**2.2 ms**) |   12 KB  | **~1000× faster, 0.5% alloc** |

### How to read this

- **At `NotificationCost = 0` the naive path wins on raw CPU.** With a no-op
  subscriber there is no downstream work to avoid, and Rambla only adds dirty
  tracking and a lock per write. This is the honest caveat, and we lead with it:
  Rambla is not a faster setter.
- **At any realistic per-notification cost, Rambla wins by ~1000×.** Because it
  delivers ~100 notifications instead of 100,000, the downstream cost — the part
  a real UI actually pays — collapses. 2.2 seconds becomes 2.2 milliseconds.
- **Allocations drop ~200× regardless** (2,344 KB → 12 KB): the naive path
  allocates a `PropertyChangedEventArgs` per event; coalescing raises a handful.

The thesis in one line:

> We don't make `PropertyChanged` faster. We make it so you never emit the
> 99,900 unnecessary `PropertyChanged` — and never pay their downstream cost.

## Secondary: micro-costs (honesty check)

`MicroCostBenchmark` isolates the per-write overhead Rambla adds (dirty tracking,
flag arming, batch scope), with the flush dropped so only the write path is
measured. These numbers exist so we can say "the write path is competitive"
without overclaiming — they are **not** the argument for the library.

## The live picture: the demo

Micro-benchmarks can't show dispatcher saturation or frame latency. The
[market dashboard sample](./samples/Rambla.Demo.MarketDashboard) does: run the
same synthetic feed through Naive, Rambla-Immediate and Rambla-Coalesced and
watch incoming mutations/s, `PropertyChanged`/s, coalescing %, flush p50/p95/p99
and UI lag live.
