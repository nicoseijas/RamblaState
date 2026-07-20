# Contributing & Design Guidelines

These guidelines keep Rambla focused, consistent and true to its
[vision](./VISION.md). Read them before opening a PR or proposing an API.

## 1. Project language

English is the primary language for **all** code, comments, documentation,
commit messages, issues and discussions. This keeps the international community
inclusive. Keep prose clear and simple; many readers are non-native speakers.

## 2. The one rule that matters most

> **The core must never know about the UI framework.**

`Rambla` (core) must not reference `Dispatcher`, `Application.Current`,
`DispatcherPriority`, `Visibility`, or any WPF/WinUI/Avalonia type. Every hop to
the UI thread goes through the `IStateScheduler` abstraction. Framework-specific
code lives only in adapter packages:

```
Rambla            → core, framework-agnostic
Rambla.Wpf        → DispatcherStateScheduler
Rambla.WinUI      → WinUI scheduler
Rambla.Avalonia   → AvaloniaStateScheduler
```

A PR that leaks a UI type into the core will be rejected. This is what prevents
Rambla from becoming "another dead library inside WPF."

## 3. Scope discipline (what NOT to build)

Rambla owns a narrow problem: **making the background → UI boundary safe and
cheap under high-frequency load.** Before adding a feature, check it against the
[non-goals](./VISION.md#explicit-non-goals):

- Don't reimplement `RelayCommand`/`AsyncRelayCommand` — that's
  CommunityToolkit.Mvvm.
- Don't build a reactive collection query engine — that's DynamicData.
- Don't build an Rx/streams composition layer — that's ReactiveUI.
- Don't grow an attribute DSL (`[Validate]`, `[AlsoNotify]`, `[Notify]`, …).
  Each new attribute must earn its place by serving the high-frequency thesis.

If a proposal makes Rambla look like "a modern MVVM framework," it's out of
scope.

## 4. API design principles

- **80% understandable from one example.** A newcomer should grasp most of the
  library from the `[State]` + write-from-any-thread snippet. Optimize the
  common path.
- **No ceremony at call sites.** The user writes `vm.Bid = 1.2345m` from a
  worker. No `Dispatcher.Invoke`, no `OnPropertyChanged`, no
  `SynchronizationContext.Post`. Rambla knows where to notify.
- **Coalescing is the natural default**, not an opt-in users must remember.
- **Framework-neutral abstractions.** Expose `StatePriority.Realtime`, not
  `DispatcherPriority`. Never couple public API to one framework's enum.
- **Name the consistency level honestly.** `BeginUpdate()`/batches provide
  *notification coherence* (observers are never notified mid-batch). They do
  **not** provide *state atomicity* — a background reader can still see a new
  `Bid` beside a stale `Ask` before the batch closes. Cross-thread snapshot
  consistency is the job of immutable **snapshots**. Never document a batch as
  "atomic" without qualifying which level you mean.
- **Prefer the framework-native expression of a pattern** where one already
  exists (`ICommand`, `INotifyPropertyChanged`) over a textbook reinvention.

## 5. Coding style (C# / .NET)

- Target current LTS (.NET 8/9+). Enable `Nullable`, `ImplicitUsings`, and
  `TreatWarningsAsErrors` via `Directory.Build.props`.
- Enable .NET analyzers (`AnalysisLevel = latest-recommended`) and keep a
  checked-in `.editorconfig` as the style source of truth. Run `dotnet format`,
  don't hand-format.
- Use Central Package Management (`Directory.Packages.props`) and pin the SDK
  with `global.json`.
- **Immutability first.** Snapshots are immutable records. Prefer returning new
  objects over mutating shared ones, except in the deliberately mutable,
  lock-protected hot path of the state engine (document those spots).
- **Many small, focused files** (200–400 lines typical, 800 hard max). Organize
  by feature (`Scheduling/`, `Batching/`, `Collections/`), not by type.
- Small functions (<50 lines), shallow nesting (≤4 levels).
- No magic values — use named constants (e.g. default `MaxRefreshRate`).
- Comment the **why**, not the **what**. Delete commented-out code.

## 6. Threading & concurrency (the heart of Rambla)

This library exists to get threading right, so hold it to a high bar.

- The state engine is written from any thread; UI notification happens on the UI
  thread via `IStateScheduler`. Keep that boundary crisp.
- No blocking on the UI thread: no `.Result`, `.Wait()`, `Thread.Sleep`. Use
  `await Task.Delay`.
- Library code uses `ConfigureAwait(false)`; ViewModel/UI code omits it.
- `Task.Run` is for CPU-bound work only; never wrap async I/O in it.
- Every long-running async path takes and flows a `CancellationToken`. Treat
  `OperationCanceledException` as normal cancellation, not an error.
- Collections built off-thread are assigned/mutated on the UI thread; never
  mutate a bound `RamblaList<T>` from a background thread outside its
  batching API.
- Document the memory model of every shared field (what lock guards it, what's
  volatile, what's lock-free and why).

## 7. Architecture map

```
Rambla
├── Core          RamblaState · StateProperty · StateTransaction
├── Scheduling    IStateScheduler · ImmediateScheduler · SyncContextScheduler
├── Batching      UpdateBatch · DirtyPropertySet · CoalescingQueue
├── Collections   RamblaList<T> · RamblaDictionary<K,V>
├── Commands      AsyncStateCommand
├── Generators    StateGenerator · StateCommandGenerator
└── Diagnostics   StateDiagnostics
```

Adapters (`Rambla.Wpf`, `Rambla.WinUI`, `Rambla.Avalonia`) implement
`IStateScheduler` and nothing else the core couldn't do itself.

## 8. Source generators

- Generators produce `INotifyPropertyChanged` plumbing, snapshot mapping and
  command lifecycle. Prefer source generation over reflection (faster,
  trim/AOT-safe).
- Generated code must be debuggable and readable — emit clear names, no magic.
- Every generator feature ships with tests that assert on the generated output.

## 9. Testing

- Stack: `xUnit` + `NSubstitute` + `FluentAssertions`.
- Test the engine as plain C#: substitute `IStateScheduler` with a synchronous
  test scheduler; assert on coalescing counts, batch notification coherence, notification
  counts. No real UI thread required.
- Cover the concurrency contracts: concurrent writers, coalescing correctness
  (latest-wins), batch notification coherence (no mid-batch notify), the
  flush/dirty/scheduled-flag race (a write landing during a flush must not be
  lost), and cancellation.
- Assert that source-generated properties raise `PropertyChanged` correctly.
- Don't test the framework itself; aim coverage at logic and edge cases.
- Benchmarks are part of the deliverable, not an afterthought — see §10.

## 10. Benchmarks

- Use BenchmarkDotNet.
- Measure the **right** axis: UI-thread CPU, dispatcher hops, notification
  count, frame latency, allocations under sustained load. Never claim a win on
  per-setter nanoseconds.
- Keep comparisons fair and reproducible: naive `PropertyChanged`,
  CommunityToolkit.Mvvm, Rambla immediate, Rambla coalesced @60Hz.
- Publish methodology alongside numbers.

## 11. Commits & PRs

- Conventional commits: `feat:`, `fix:`, `refactor:`, `docs:`, `test:`,
  `perf:`, `chore:`, `ci:`.
- One logical change per PR. Include tests. Update docs when public API changes.
- Run `dotnet test`, `dotnet format --verify-no-changes`, and
  `dotnet list package --vulnerable` before requesting review.

## 12. Definition of done

- [ ] Core stays framework-agnostic (no UI types leaked).
- [ ] Public API reads cleanly in one example.
- [ ] Threading contracts documented and tested.
- [ ] Tests pass, ≥80% coverage on new logic, format clean.
- [ ] Docs updated (README/VISION/ROADMAP as relevant).
- [ ] Benchmarks added/updated for performance-sensitive changes.
