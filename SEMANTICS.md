# Rambla core semantics — frozen contract (V1)

These are the behavioural guarantees of `RamblaState` as of V1. They are frozen:
the `[State]` source generator and every adapter are built on top of them, so
changing one is a breaking change. Each is backed by tests.

## 1. Subscriber exceptions — fail-fast, never wedged

If a `PropertyChanged` subscriber throws during a flush, the exception
**propagates** (fail-fast). Rambla does not swallow consumer exceptions.

Guaranteed regardless:

- The engine is left **schedulable** — the scheduled flag is cleared and the
  dirty set is emptied *before* any notification is raised, so no state is
  orphaned. A subsequent mutation flushes normally.
- Remaining notifications in the aborting flush are **not** raised. Fail-fast
  trades completeness of that one flush for not hiding the error. Their values
  are already applied to the fields; the next write to those properties notifies.

*Tested:* `ConcurrencyTests.Throwing_subscriber_does_not_wedge_the_state`.

## 2. Notification order — unspecified

Within a single coalesced flush, the order of `PropertyChanged` events is **not
guaranteed** (the dirty set is unordered). Do not depend on `Bid` notifying
before `Ask`. This frees future scheduling/coalescing optimizations. All dirty
properties *are* notified exactly once per flush; only their order is unspecified.

## 3. Equality and the meaning of "mutation"

`SetField` compares with `EqualityComparer<T>.Default`. If the new value equals
the current one:

- the field is not written,
- the property is not marked dirty,
- no notification is raised,
- **no mutation is counted.**

`StateMetrics.Mutations` therefore counts **actual state changes**, not write
attempts. `Price = 100; Price = 100; Price = 100;` is one mutation, not three.

*Tested:* `SemanticsContractTests.Metrics_counts_actual_mutations_not_attempts`.

## 4. Batching

- Leaving the **outermost** `BeginUpdate()` scope schedules **exactly one** flush
  — unless no real change occurred inside, in which case **no** flush is
  scheduled.
- With nested batches, only disposing the outermost scope enables scheduling.
- Scopes may be disposed in **any order** (the depth is a counter, not a stack).
- Disposing a scope more than once is a **no-op**.

*Tested:* `ConcurrencyTests.Nested_BeginUpdate_*`, `*_out_of_order_*`,
`Double_dispose_*`; `SemanticsContractTests.Empty_batch_schedules_no_flush`.

## 5. Scheduler may be synchronous or deferred

`IStateScheduler.Post` may invoke the flush **inline** (synchronously, like
`ImmediateStateScheduler`) or **later** (asynchronously, like a dispatcher). The
engine is correct under both: it holds no temporal assumptions about when a
posted flush runs, and posts the flush **outside** its lock so an inline
scheduler cannot raise notifications while the lock is held.

*Tested:* the suite runs the same assertions under `ImmediateStateScheduler` and
a deferred `ManualStateScheduler`.

## 6. Lifetime and ownership

`RamblaState` **owns no resources** and is intentionally **not `IDisposable`**.

- It does not own or dispose its `IStateScheduler`; scheduler lifetime is the
  **caller's** responsibility. (A disposable scheduler such as the demo's
  `ThrottledDispatcherScheduler` is owned by whoever created it.)
- There is no teardown step. A `RamblaState` that is no longer referenced is
  simply collected. Writes from a background thread to a still-referenced state
  remain valid; there is no "closed" state in V1.

This is a deliberate V1 decision to keep the base class free of accidental
lifecycle. If disposal is ever needed, it will be an explicit, additive opt-in.

## Observability — probes do not change behaviour

`RamblaState.AttachProbe(IStateProbe)` attaches a diagnostics observer (used by
the `Rambla.Diagnostics` package). A probe is a **pure observer**: it is invoked
*after* a mutation is accepted and *after* a flush's notifications are raised, and
it changes none of the contracts above — notification order, coalescing,
coherence, and exception behaviour are identical whether or not a probe is
attached. When no probe is attached, the hot path pays a single volatile read.

A probe is documented to never throw, but the engine does not trust it: a probe
whose callback throws is **isolated** — its exception is swallowed and neither
stops other probes nor prevents the flush from being scheduled. (This is stricter
than the `PropertyChanged` contract, which fails fast: a diagnostics observer must
never be able to affect the app it observes.)

*Tested:* the suite asserts notification behaviour is unchanged with a probe
attached, that no-op writes are not reported as mutations, and that a throwing
probe neither wedges the pipeline nor silences other probes.
