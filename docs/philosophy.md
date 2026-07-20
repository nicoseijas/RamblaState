# Philosophy: Rambla is for state, not events

The single most important thing to understand about Rambla is *when not to use
it*. Coalescing drops intermediate values on purpose. That is a feature for
**state** and a bug for **events**.

## State vs. events

- **State** answers *"what is the value now?"* Intermediate values are
  disposable. If `BTC.Price` ticks five times before the next frame, the UI only
  needs the last one. Dropping the other four is correct.
- **Events** answer *"what happened?"* Each occurrence carries meaning. If five
  trades arrive, you must process five. Coalescing them into "the last trade"
  loses data.

Rambla is a **state** engine. Its whole value is turning a high-frequency stream
of *values* into the minimum number of UI-visible *updates*. Point it at event
semantics disguised as state and it will silently discard information.

> If every intermediate value matters, do not use Rambla. Use a queue, a stream,
> or a change-set (e.g. DynamicData) that preserves each occurrence.

## The founding sentence

> **Rambla does not make the notification cheaper. It avoids emitting it.**

It trades a small, constant local overhead (synchronization + dirty tracking +
scheduling — roughly a few× the cost of a bare setter) for orders-of-magnitude
less downstream work, *when the state changes faster than the UI can consume it*.

## Where Rambla fits

**Great fit** — high-frequency, high-repetition state:

- market data (bid/ask/last/PnL)
- telemetry and device/software status
- monitoring dashboards
- progress and throughput counters
- game/simulation state
- anything driven by a WebSocket or timer at hundreds–thousands of updates/sec

**Poor fit** — low repetition or event semantics:

- CRUD forms and settings screens (property changes are rare; bookkeeping is pure cost)
- streams where every intermediate value must be observed (fills, trades, log lines)
- wide fan-outs where each flush touches thousands of *distinct* properties once
  (coalescing ratio → 0%; see the burst-shape results in
  [../BENCHMARKS.md](../BENCHMARKS.md))

## The empirical boundary

The benchmarks make this concrete. Coalescing benefit tracks the **entropy** of
the update stream:

- Many writes to few properties per flush window → coalescing near 100% → Rambla wins.
- One write each to many distinct properties per window → coalescing near 0% →
  Rambla is overhead and *loses*.

We publish the losing cases deliberately. A tool that claims to help everywhere
helps nowhere in particular; Rambla's value is sharp precisely because its domain
is bounded.
