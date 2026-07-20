# Roadmap

This roadmap sequences Rambla from a sharp MVP to a multi-framework library.
The ordering is deliberate: **prove the core thesis first** (background → UI
under load), then expand. See [VISION.md](./VISION.md) for the why and
[GUIDELINES.md](./GUIDELINES.md) for the how.

## Guiding principle

Ship the smallest thing that proves the thesis. The library becomes memorable
not through breadth of attributes, but through the triad:

```
background writes → coalesced UI snapshots → diagnostics that prove
                                             how much work you avoided
```

---

## MVP — five primitives + one demo

The minimum that demonstrates real value. Nothing more.

> **Status:** all five primitives implemented and tested; the market dashboard
> demo runs; the `[State]` generator (V1) ships and is dogfooded by the demo. The
> core semantics are frozen (see [SEMANTICS.md](./SEMANTICS.md)) and the
> benchmark thesis is validated (see [BENCHMARKS.md](./BENCHMARKS.md)).

1. **`RamblaState`** — the base class you inherit.
   ```csharp
   public partial class QuotesViewModel : RamblaState { }
   ```
2. **`[State]`** — source-generated observable properties.
   ```csharp
   [State] private decimal _bid;
   ```
3. **`BeginUpdate()`** — explicit batch with coherent notification (no mid-batch notify).
   ```csharp
   using (BeginUpdate()) { Bid = bid; Ask = ask; }
   ```
4. **`StateScheduler`** — captures the UI context automatically and marshals
   background writes into UI notifications, via `IStateScheduler`.
5. **Coalescing** — latest-value-wins, configurable refresh rate.
   ```csharp
   RamblaOptions.Default.MaxRefreshRate = 60;
   ```

**Flagship demo** — a real-time market dashboard:

```
100 symbols · Bid / Ask / Last / Volume / PnL
fake feed @ 50,000 updates/sec
live counters: incoming updates · UI notifications · FPS · dispatcher latency · CPU
```

The demo sells the library on its own.

---

## Phase 1 — State engine

- `RamblaState` base + `[State]` generator
- `PropertyChanged` plumbing
- Thread marshaling via `IStateScheduler`
- `BeginUpdate()` transactions
- Coalescing (latest-value-wins) at a configurable refresh rate
- BenchmarkDotNet suite measuring UI-thread CPU, dispatcher hops, notification
  count, frame latency, allocations under sustained load

**Exit criteria:** the flagship benchmark shows Rambla collapsing tens of
thousands of mutations/sec into ~60 coherent notifications/sec, reproducibly.

## Phase 2 — High-frequency collections

- `RamblaList<T>` — batched changes, single aggregated notification
- `Batch(...)` API for grouped add/remove/update
- `ReplaceSnapshot(...)` with efficient diffing (minimal UI changes)
- `RamblaDictionary<K,V>`

**Scope guard:** we do *not* build a reactive query engine — that's
DynamicData. We only make the collection → UI boundary cheap.

## Phase 3 — Async lifecycle

- `AsyncStateCommand` / `[StateCommand]`
- Auto-generated busy/error state (`IsRefreshing`, `RefreshError`)
- Auto-generated cancel command
- Latest-wins with `CancelPrevious = true` (e.g. as-you-type search cancels the
  prior request)

**Scope guard:** this complements, not replaces, CommunityToolkit's
`AsyncRelayCommand`. It exists only where concurrent *state* lifecycle adds
value.

## Phase 4 — Diagnostics (the flagship)

- `StateDiagnostics.Attach(viewModel)`
- Metrics: mutation rate, notification rate, coalescing ratio, dispatcher
  latency, longest flush, UI-thread budget, dropped intermediate values
- Hot-property detection
- Actionable recommendations, e.g.
  *"Positions generated 14,281 individual notifications/sec — use Batch() or
  ReplaceSnapshot()."*

This is what turns an invisible cost into an observable, provable one.

## Phase 5 — Framework adapters

- `Rambla.Wpf` — `DispatcherStateScheduler` (first, ships alongside MVP demo)
- `Rambla.WinUI`
- `Rambla.Avalonia`
- Possibly `Rambla.Maui`

The core never learns about `Dispatcher`; each adapter only implements
`IStateScheduler`.

---

## Beyond the roadmap (candidate ideas, unscheduled)

- Frequency policy per property (`[State(UpdateRate = 60)]`)
- Priority tiers (`StatePriority.Realtime/Normal/Background`) over a
  framework-neutral abstraction
- Derived state (`[DerivedState(nameof(Bid), nameof(Ask))]`) — only valuable
  *combined* with batching, so it notifies once per flush
- Snapshot-typed state (`RamblaState<TSnapshot>` + `Publish(snapshot)`)

These are explicitly deferred until the MVP + diagnostics prove the core thesis.
Adding them early risks turning Rambla into "CommunityToolkit, but mine" — the
one failure mode we refuse.
