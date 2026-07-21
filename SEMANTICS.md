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
- **No flush runs while a batch is open.** A flush that was armed and posted
  *before* the batch opened may be dispatched while the batch is still in
  progress; it defers entirely (delivering it would raise the batch's own
  accumulated writes mid-batch). Closing the outermost scope re-arms a fresh
  flush, so the pre-batch writes are simply delivered together with the batch —
  coherence is preserved at the cost of that one deferral.

*Tested:* `ConcurrencyTests.Nested_BeginUpdate_*`, `*_out_of_order_*`,
`Double_dispose_*`; `SemanticsContractTests.Empty_batch_schedules_no_flush`,
`SemanticsContractTests.Flush_armed_before_a_batch_does_not_run_while_the_batch_is_open`.

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

## Cross-thread reads — stale, and possibly torn for wide types

A property getter reads its backing field directly, with no synchronization —
that is what keeps the read path free. Two consequences for a reader on a
different thread than the writer:

- **Staleness.** A concurrent reader may see a value from before the latest
  write, or a new value beside a stale one mid-batch. This is the documented
  absence of cross-thread state atomicity.
- **Tearing.** For types wider than a machine word (`decimal`, large structs,
  `DateTime`/`long` on 32-bit), the runtime does not guarantee atomic reads: a
  read that races a write can observe a mix of old and new bytes — a value that
  was **never written**. The window is nanoseconds, but at high write rates the
  exposure is real.

Reads made **in reaction to a notification** (the normal binding path) are safe
on both counts: the flush acquires the state's lock before raising, which
establishes happens-before with every write it notifies. Tearing only concerns
reads that race the writer from another thread outside that path. If you need
race-free wide values, read them from a `PropertyChanged` handler, publish an
immutable snapshot object (reference writes are atomic), or keep the value in a
word-sized type.

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

## Collections — `RamblaList<T>`

`RamblaList<T>` applies the same mutation ≠ notification model to a collection,
with the extra invariant WPF requires: the visible contents must always agree with
the change events already raised.

1. **Reads are the flushed state.** `Count`, the indexer, and enumeration reflect
   the last flushed (UI-visible) contents — never the pending target. A mutation is
   not observable to a reader until the next flush. (This differs from
   `RamblaState`, whose property getters return the new value immediately; a
   collection cannot, without violating WPF's Count/`CollectionChanged` invariant.)
2. **One coalesced flush.** A burst of mutations (or a `BeginUpdate` batch) produces
   one flush. The flush diffs the previous visible contents against the new target
   by `EqualityComparer<T>.Default` and raises the **minimum** `Add`/`Remove`/
   `Replace` events — or a single `Reset` when more than `ResetThreshold` (default
   32) elements changed. A net-zero batch (add then remove) raises nothing.
   Batching follows §4, including its deferral rule: no flush applies while a
   batch is open, even one armed before the batch. `ReplaceSnapshot` materializes
   its argument up front — if enumerating the source throws, the list is left
   exactly as it was, with nothing pending.
3. **No moves in V1.** Reordering the same instances is reported as replacements,
   not `Move` events. Keep stable row instances and update their fields via
   `RamblaState`; the list then changes only on real add/remove.
4. **Threading.** Mutators are safe from any thread. Reads and raised events belong
   to the scheduler's thread (the UI thread under a dispatcher adapter). The
   non-generic `IList` view is **read-only** (so WPF uses a virtualizing
   `ListCollectionView`); all writes go through the typed mutators.
5. **Flush execution is serialized.** At most one flush applies to the visible list
   at a time, even under an inline scheduler with concurrent writers, or when a
   `CollectionChanged` handler mutates the list re-entrantly. A flush that finds one
   already running bails out; the running flusher drains all pending changes.

*Tested:* deferred visibility, add/remove/replace index correctness, prefix/suffix
minimal diffs, Reset fallback, net-zero coalescing, concurrent writers landing in
the final state, no corruption under concurrent/reentrant flush on an inline
scheduler, no flush delivery while a batch is open, and `ReplaceSnapshot` having
no effect when its source throws mid-enumeration.
