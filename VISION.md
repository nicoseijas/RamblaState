# Vision

## One sentence

> Rambla makes the **background → UI boundary** safe and cheap for .NET desktop
> applications whose state changes faster than it can be rendered.

## The metaphor

A *rambla* is a waterfront promenade: many parallel flows moving continuously
along one shared surface. That is the mental model — countless independent
streams of state (prices, positions, telemetry, trades) all converging on a
single, thread-affine UI surface. The art is letting them keep moving without
blocking that surface.

Tagline: **State that keeps moving without blocking the UI.**

## The problem we actually solve

The instinct is to blame `INotifyPropertyChanged`. That is the wrong target.
A property setter and a `PropertyChanged` event are already cheap.

The real cost is structural:

1. WPF (and XAML frameworks generally) have **thread affinity** around a single
   UI `Dispatcher` that also handles input, layout and rendering.
2. High-frequency producers (WebSockets, timers, workers) generate **far more
   updates than the UI can render** — often thousands per second.
3. Every naive update becomes a dispatcher hop, a binding pass, a layout
   invalidation. Multiplied by the update rate, the UI thread saturates.

Rambla's core insight:

> **State mutation ≠ UI notification.**

Once those two are decoupled, coalescing, batching, snapshotting and throttling
all become natural. You can absorb 50,000 mutations/sec and emit ~60
coherent UI notifications/sec.

## Positioning

The .NET MVVM space is crowded, and we deliberately avoid its saturated lanes:

- **CommunityToolkit.Mvvm** already nails boilerplate: `ObservableObject`,
  source-generated observable properties, `RelayCommand`/`AsyncRelayCommand`.
- **ReactiveUI** already nails reactive composition, schedulers and derived
  observables.
- **DynamicData** already nails reactive collection queries via `IChangeSet<T>`.

We do **not** try to be "a modern MVVM framework" or "reactive state management
for .NET." Both are overcrowded and undifferentiated. Rambla claims a narrow,
defensible niche instead:

> **High-frequency, thread-safe, snapshot/batching-oriented UI state for
> real-time .NET desktop applications.**

## The memorable triad

The feature that gives Rambla its identity is **not** the `[State]` attribute.
Anyone can generate observable properties. It is this pipeline:

```
background writes  →  coalesced UI snapshots  →  diagnostics that prove
                                                 how much work you avoided
```

Diagnostics is the differentiator that makes an invisible problem visible —
the equivalent of a `diagnostics.check()` for real-time UI state. Being able to
show "18,420 mutations/sec collapsed into 58 notifications/sec, 99.7%
coalesced, UI thread at 17% budget" is what sells the library.

## The flagship benchmark

We measure the right thing. We will **not** try to win on nanoseconds per
setter — CommunityToolkit is explicitly optimized for that and it is the wrong
axis anyway.

Instead, under **sustained high-frequency load** we measure:

- UI-thread CPU
- dispatcher operations
- binding notifications
- frame latency
- allocations

Illustrative shape of the result (numbers are a target design, not a claim):

```
100,000 producer updates          UI notifications   Dispatcher hops
Naive PropertyChanged                     100,000           100,000
CommunityToolkit.Mvvm                     100,000    caller-dependent
Rambla @60Hz                                  420                60
```

The thesis:

> We don't make `PropertyChanged` 20 ns faster.
> We make it so you never emit the 99,000 unnecessary `PropertyChanged` at all.

## Design tenets

- **The core is framework-agnostic.** It must never reference `Dispatcher`,
  `Application.Current`, or any WPF/WinUI/Avalonia type. All UI marshaling goes
  through `IStateScheduler`. This is what stops Rambla from becoming "another
  library that only lives inside WPF."
- **Simplicity over surface area.** We start with five primitives, not fifteen
  attributes. A large attribute vocabulary (`[Validate]`, `[AlsoNotify]`, …)
  would just make us "CommunityToolkit, but mine." That is a failure mode.
- **Not Rx.** ReactiveUI already owns mature reactive streams. Rambla is
  deliberately simpler: mutable state + thread-safe publishing + coalesced
  notifications + batching + diagnostics.
- **Throttling is a property of state**, not per-ViewModel plumbing.
- **Two levels of consistency, named honestly.**
  - *Notification coherence* (what `BeginUpdate()`/batches provide today):
    observers are never notified mid-batch, so bindings re-render on the batch's
    final values together instead of on a half-applied `Bid` with a stale `Ask`.
  - *State atomicity* (what immutable **snapshots** provide): every reader,
    on any thread, observes one internally consistent set of values. A raw
    batched write does **not** guarantee this — a background reader can still see
    a new `Bid` beside an old `Ask` before the batch closes. When you need
    cross-thread snapshot consistency, publish a snapshot, not individual writes.

## Explicit non-goals

- Replacing CommunityToolkit.Mvvm, ReactiveUI or DynamicData.
- A full reactive query engine over collections (that is DynamicData).
- Winning micro-benchmarks on per-setter cost.
- A sprawling attribute-driven MVVM DSL.
- Being an Rx layer.

## Who it's for

Engineers building trading terminals, dashboards, telemetry and monitoring
tools, poker tables, market-data displays, device/status panels, and any
WebSocket-driven desktop UI that receives more updates than it can draw.
