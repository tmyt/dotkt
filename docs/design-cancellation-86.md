# #86 — CancellationToken into the coroutine ABI (PLAN)

> Status (2026-07-12): **PLAN ONLY** (user-directed: "#86 も plan だけでいい"). **Verdict: NOT needed for 1.0.**
> Defer P1–P3 to Track 2 (kotlinx port). One independent ~5-line correctness nit (P0) is worth doing whenever the
> user green-lights it.

## Necessity verdict — NOT needed for 1.0 (the user's skepticism is correct)
On this platform cancellation is purely an INTEROP concern, and the two cases that matter already work today with
ZERO ABI changes. An "ABI CancellationToken" only buys kotlinx-shaped convenience, which is definitionally Track 2.
The design already locked this: `docs/coroutine-abi.md:448` v1 limits list "No CancellationToken/Job/interceptor
dispatch (later layers)".

- **Case A — awaiting a cancellable .NET Task (the common case): works today.** The only genuine suspension point
  is `Task.await()`. A cancelled awaited Task makes `GetResult()` throw `OperationCanceledException`
  (`SuspendColdLowering.cs:1828-1834`), caught by `BaseContinuationImpl.resumeWith`
  (`ContinuationImpl.kt:56-58`) → `RootContinuation.resumeWith` (`RootContinuation.kt:26-33`) → the public
  `Task<T>` completes failed. End-to-end propagation, no ABI slot.
- **Case B — a DotKt author honoring a caller's CT: works today via ordinary interop.** `CancellationToken` is a
  normal BCL struct facadegen surfaces; a suspend fn takes `ct: CancellationToken` as a plain parameter, passes it
  to `Task.Delay(ms, ct)`, and calls `ct.ThrowIfCancellationRequested()` by hand. No compiler work — the CT is
  just a value.

Everything left over (implicit `currentCancellationToken()`, auto-inserted checks, a synthesized trailing-CT
bridge param) only has meaning inside structured concurrency (Job/CoroutineScope), which does not exist here
(kotlinx purged) — so there is no Kotlin-side CT producer to connect to until Track 2.

## Design principle (user, 2026-07-12) — cancellation stays EXPLICIT interop; the compiler embeds NO cancellation dialect
Auto-insertion (`ThrowIfCancellationRequested()` auto-emitted at suspension points, implicit CT threading, an
implicit `currentCancellationToken()`) **drags kotlinx/structured-concurrency vocabulary INTO the compiler** — the
opposite of this project's core (kotc is coroutine-symbol-free / pure binding / "discard JVM-isms on CLR" / the
`@ClrIntrinsic` "bind by metadata, embed no dialect" philosophy). So the RIGHT model is Case B, permanently: the
author writes cancellation **explicitly** (declare `ct: CancellationToken` as an ordinary parameter, pass it to
`Task.Delay(ms, ct)`, call `ct.ThrowIfCancellationRequested()` by hand). **The compiler never knows "cancellation"
as a concept** — a `CancellationToken` is just a BCL struct value flowing through ordinary interop. This DEMOTES
P1/P3 below from "deferred to Track 2" to "**design-questionable — reconsider before building at all**": even in
Track 2, whether to REPRODUCE kotlinx's structured cancellation is itself an open question (consistent with
discarding JVM-isms, the answer may be "don't"). **P0 is the exception** — it is not a dialect but pure .NET-Task
fidelity (a cancelled operation yields a Canceled Task), correct independent of any of this.

## The one thing worth doing NOW — P0 (independent, ~5 lines)
`RootContinuation.resumeWith` (`RootContinuation.kt:28-30`) maps EVERY exception — including
`OperationCanceledException` — to `tcs.trySetException(...)` (a Faulted Task). .NET convention: a cancelled
operation yields a CANCELED Task (`tcs.TrySetCanceled(oce.CancellationToken)`), so `task.IsCanceled` reads true.
Fix: special-case `OperationCanceledException` → `TrySetCanceled`. Correct regardless of #86's fate. Gate: a
coroutine case that awaits a pre-cancelled Task and asserts the public Task `IsCanceled`.

## IF later built — the minimal design (Track 2)
Carrier: **a `CoroutineContext` element, NOT a new SM field and NOT the erased cold-entry signature.** The cold
entry `f$dotkt_suspend(args…, completion)` (`SuspendColdLowering.cs:12-13`) must stay erased/JVM-equivalent for
cross-assembly linkage (`coroutine-abi.md:415-420`); the `_context` slot (`ContinuationImpl.kt:122-129`) already
exists and is designed for exactly this (it is how kotlinx's `Job` rides the context) — inherited down the
completion chain, read via a future `currentCancellationToken()`.
Smallest-first:
1. Bridge entry: optional trailing `CancellationToken` on `BuildBridge()` (`SuspendColdLowering.cs:2668`); seed the
   RootContinuation/ContinuationImpl context with a CT element (the C# `Foo(args, ct)` idiom).
2. Suspension-point check: at `EmitSuspensionPoint`/`EmitAwaitPoint` (`SuspendColdLowering.cs:1759`), before
   returning `COROUTINE_SUSPENDED`, emit `ct.ThrowIfCancellationRequested()` guarded on "context carries a CT".
   (Literally the user's `ThrowIfCancellationRequested()` — that is the whole runtime story.)
3. Await honoring: thread the context CT into a `Task.Delay`/CT-accepting awaited call (optional — the awaited
   Task usually already carries a CT, Case A).
4. P0's OCE→Canceled fix (above).
5. Kotlin surface (only if desired): a single `kotlin.clr.currentCancellationToken(): CancellationToken` reading
   the context element. NO `withCancellation` scope (that is Job/Track 2).
No `Job`, no structured tree, no `CoroutineScope`.

## Phasing / ownership / effort
| Phase | Scope | Layer | Effort |
|---|---|---|---|
| **P0 (do when green-lit)** | OCE → `TrySetCanceled` in `RootContinuation.resumeWith` | stdlib (`taskinterop/`) | ~5 lines |
| P1 (defer, Track 2) | context CT element + `currentCancellationToken()` | stdlib cold-core + `kotlin.clr` | days |
| P2 (defer) | bridge trailing-CT param + seed context | bir2cir `SuspendColdLowering.BuildBridge` | days |
| P3 (defer) | `ThrowIfCancellationRequested()` at suspension points + CT into `Task.Delay` | bir2cir `EmitSuspensionPoint`/`EmitAwaitPoint` | days |
| Track 2 | Job/scope/`withContext`/`suspendCancellableCoroutine` | kotlinx port | weeks |

kotc stays CT-unaware (pure suspend facts); all CT logic lives in bir2cir + stdlib; ilemit stays coroutine-free.
**Bottom line: ship 1.0 without an ABI CancellationToken.** Do the ~5-line P0 whenever convenient; defer the rest.
