# Rambla

> **High-frequency observable state for real-time .NET desktop applications.**
>
> Thread-safe updates, batching, coalescing and diagnostics for UI state that
> changes faster than it can be rendered.

Named after the Uruguayan *rambla* — many parallel flows moving continuously
along a shared surface. That is exactly the problem Rambla solves: hundreds or
thousands of state changes per second, arriving from background threads, that
must reach the UI **without saturating it**.

---

## The problem

WPF and other XAML frameworks have thread affinity around a single UI
`Dispatcher`. That thread processes input, layout and rendering. When a feed
pushes updates at high frequency, the naive path looks like this:

```
WebSocket / worker / timer
        ↓  2,000 updates/sec
ViewModel
        ↓
PropertyChanged × N
ObservableCollection change × N
Dispatcher.Invoke × N
        ↓
UI thread saturated
```

The problem is **not** `INotifyPropertyChanged`. The problem is emitting tens of
thousands of notifications the UI cannot possibly render, one dispatcher hop at
a time.

## The idea: mutation ≠ notification

Rambla separates *state mutation* from *UI notification*. You write state from
any thread; Rambla decides when and how the UI is told, coalescing intermediate
values and batching notifications into a single dispatcher hop per frame.

```csharp
public partial class MarketViewModel : RamblaState
{
    [State] public decimal Bid { get; set; }
    [State] public decimal Ask { get; set; }
    [State] public decimal PnL { get; set; }
}
```

From any background worker:

```csharp
vm.Bid = 1.2345m;
vm.Ask = 1.2346m;
vm.PnL = 23m;
```

No `Dispatcher.Invoke`, no `OnPropertyChanged(...)`, no
`SynchronizationContext.Post(...)`. Rambla already knows where and when to
notify.

Instead of `4 dispatcher calls → 4 PropertyChanged → 4 binding passes`, the
runtime does:

```
worker writes → dirty state → coalesce → UI flush (~16 ms) → batched notification
```

## What makes it different

- **Coalescing** — latest value wins. A price that ticks five times in 5 ms
  notifies the UI once, with the final value.
- **Explicit batching** — coalesce writes into one *coherent notification pass*:
  bindings are never notified mid-batch, so the UI re-renders on the batch's
  final values together, not on a half-applied `Bid`/stale `Ask`. (This is
  notification coherence, not cross-thread state atomicity — use snapshots for
  that.)
- **Snapshots** — publish an immutable snapshot as a single consistent unit;
  the state-atomicity path, ideal for market data and telemetry feeds.
- **High-frequency collections** — `RamblaList<T>` with batched
  changes and efficient diffing, instead of one notification per item.
- **Frequency policy** — throttling becomes a property of the *state*
  (`MaxRefreshRate = 60`), not something every ViewModel re-implements.
- **Priorities** — a framework-neutral abstraction over dispatcher priority
  levels, so real-time data outranks background text.
- **Async state commands** — commands with built-in busy/error/cancel
  lifecycle and latest-wins semantics.
- **Diagnostics** — measure how much work you *avoided*: mutation rate,
  notification rate, coalescing ratio, dispatcher latency, hot properties.

## The flagship: diagnostics

```csharp
StateDiagnostics.Attach(viewModel);
```

```
MarketViewModel
  Incoming state mutations : 18,420 / sec
  UI notifications         :     58 / sec
  Coalescing               :  99.68 %
  Dispatcher hops          :     60 / sec
  Longest UI flush         :   2.8 ms
  UI thread budget         :     17 %

  ⚠ Positions collection generated 14,281 individual notifications/sec.
    Recommendation: use Batch() or ReplaceSnapshot().
```

Rambla turns something invisible — the cost of pushing background state to the
UI — into something you can observe and prove.

## Built for

Trading terminals · dashboards · telemetry · monitoring · poker tables · market
data · device/software status · WebSocket-driven apps · any UI receiving
hundreds or thousands of updates per second.

## Where Rambla fits

Rambla does **not** compete with:

- **CommunityToolkit.Mvvm** — keep using it for `ObservableObject`,
  observable-property generation and `RelayCommand`/`AsyncRelayCommand`.
- **ReactiveUI** — keep it for reactive composition and schedulers.
- **DynamicData** — keep it for `IChangeSet<T>` reactive collection queries.

Rambla owns a smaller, sharper problem:

> **Make the background → UI boundary safe and cheap under sustained,
> high-frequency load.**

## Packages

| Package         | Purpose                                        |
| --------------- | ---------------------------------------------- |
| `Rambla`        | Framework-agnostic core state engine           |
| `Rambla.Wpf`    | WPF dispatcher scheduler adapter               |
| `Rambla.WinUI`  | WinUI 3 adapter *(planned)*                     |
| `Rambla.Avalonia` | Avalonia adapter *(planned)*                  |

The core never references `Dispatcher`. Framework integration is an adapter
behind `IStateScheduler`.

## Proof

The headline is architectural, not a multiplier:

> **100,000 mutations → ~101 effective notifications — a ~99.9% reduction.**

That collapse is what makes the downstream cost disappear. Indicative result at
100,000 writes with a UI-like subscriber (convert + fan-out), from
[BENCHMARKS.md](./BENCHMARKS.md):

| Path             | Notifications | Mean       | Allocated |
| ---------------- | ------------: | ---------: | --------: |
| Naive            |       100,000 | ~11.0 ms   | 21,871 KB |
| Rambla coalesced |         ~101  | ~1.0 ms    |     32 KB |

Same producer load; ~11× faster and ~680× fewer allocations because the work was
never emitted. **Honest caveat:** with a no-op subscriber the naive path wins on
raw CPU (Rambla adds bookkeeping), and with a high-entropy stream — thousands of
*distinct* properties per flush — coalescing can't help and Rambla loses. It is a
tool for state that repeats faster than it renders, not for one-shot fan-outs.
See [BENCHMARKS.md](./BENCHMARKS.md) and [docs/philosophy.md](./docs/philosophy.md).

Run the [market dashboard demo](./samples/Rambla.Demo.MarketDashboard) to watch
it live.

## Status

Early development: the core state engine (writes, batching, coalescing,
schedulers, opt-in metrics) is implemented and covered by unit + concurrency
stress tests; the WPF adapter and demo run; the `[State]` source generator is
next. See [ROADMAP.md](./ROADMAP.md) for phases and [VISION.md](./VISION.md) for
the thesis. Contributors: read [GUIDELINES.md](./GUIDELINES.md) first.

## License

Rambla is released under the permissive [MIT License](./LICENSE), so it can be
adopted anywhere with minimal friction.

## Language

English is the primary language of this project (code, docs, issues, and
discussions) to keep the international community inclusive.
