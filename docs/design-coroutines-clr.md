# Design groundwork: coroutines on CLR / building kotlinx.coroutines

> **STATUS (2026-07-03, bundle-6 P0):** partially SUPERSEDED by
> `design-coroutine-cold-core-task-bridge.md` — the core is cold/Continuation-based; the hot Task
> ABI survives only as the boundary bridge. DotKt.Runtime placement herein is historical.


> **状態 (2026-06-30 見直し)**: HISTORICAL/AS-BUILT record of Track-1 (the standalone coroutine compiler surface). 現行アーキテクチャの正は [docs/ship-tasks.md](ship-tasks.md) §0。
>
> 補足:
> - **ABI (§1–§12) と Track-2 (本物の kotlinx-coroutines-core を `dotktx.coroutines` としてコンパイルする計画) は今も有効** — `suspend fun ⇔ Task<T>`（Continuation は internal）という ABI 契約は現行 ground truth と一致するので変更しない。
> - **ランタイム配置 (§13b / §14 / §14a の「all ship in DotKt.Runtime.dll」) は SUPERSEDED** — コルーチンランタイムは stdlib の `clr/` actuals へ移動中で、`runtime/DotKt.Runtime/Coroutines.cs` は削除予定。正は [docs/coroutine-stdlib-port-plan.md](coroutine-stdlib-port-plan.md)。この移動は **IN-FLIGHT**（当該ファイルはまだ存在し、`CoroutineBridgeClr.kt` 等のブリッジは未作成）。
> - **BirEmitter / ilemit という帰属は bir2cir レイヤ分割より前の枠組み** — 現行は 4 層（facadegen / kotc / bir2cir / ilemit）で、Kotlin↔CLR の suspend lowering は bir2cir の責務。当時の本文は BirEmitter→ilemit の 2 段で記述している。
> - 命名: `@Clr` / `@ClrAwait` は現行 `kotlin.clr.ClrIntrinsic`。

Status: **the coroutine compiler surface is FULLY IMPLEMENTED** (2026-06-22…06-23), all green + ilverify-clean.
Done (each with an `il-*` sample in scripts/verify-il.sh): suspend funs (linear/loop/branch/spilling/
condition-position suspension/try-catch- AND try-finally-around-await), suspend lambdas incl. receiver-style
(`flow { emit(x) }`), generic/Unit-result/extension suspend funs, raw intrinsics, resume API, `startCoroutine`,
`suspendCancellableCoroutine`, unified `kotlin.Result`, user classes implementing `Continuation<T>`, `Unit` as a
generic type argument, `sequence{}`/`yieldAll`/`generateSequence`, `Flow` (incl. generic) + `Flow`↔`IAsyncEnumerable`,
`Channel`, `select`, the `CoroutineContext` element algebra + `coroutineContext`, `ContinuationInterceptor`/
`intercepted` + a dispatcher. **§§1–12 below are the original design rationale (historical, written design-first);
the AS-BUILT status is recorded incrementally in §§13a–§14a.** The only remaining axis is Track 2 — compiling the
real upstream `kotlinx-coroutines-core` (everything above was proven standalone with synthetic facades, no upstream).
See memory `dotktx-coroutines-path-b`.

## 1. Goal & strategy

Don't hand-reimplement `launch`/`async`/`Flow` in the compiler. **Compile `kotlinx-coroutines-core` (commonMain)
with DotKt + a CLR `actual` source set** — exactly how Kotlin/Native and Kotlin/JS get coroutines. This:
- gives the full, real API (Flow, Channel, structured concurrency) for free, tracking upstream;
- aligns with `kotlin-net-is-pure-binding` (the compiler binds; libraries are compiled BY it, not bundled in);
- dogfoods the compiler on a large real Kotlin codebase;
- generalizes: the same path then builds kotlinx.serialization / datetime / collections-immutable / etc.

The `delay`/`runBlocking` hand-mappings shipped in #49 (commit 32f6877) are **stopgaps** — superseded once the
real library compiles.

## 2. The crux: Continuation-based vs the current Task-based ABI

kotlinx.coroutines is built on the **raw Continuation intrinsics**:
`suspendCoroutineUninterceptedOrReturn`, `COROUTINE_SUSPENDED`, `kotlin.coroutines.Continuation`,
`createCoroutineUnintercepted`, `intercepted()`, `startCoroutine`. The standard Kotlin `suspend` lowering is:
`suspend fun f(args): T`  →  `f(args, Continuation<T>): Any?` (returns the value or the `COROUTINE_SUSPENDED`
sentinel), with the body compiled to a **state machine class implementing `Continuation`** (a `label` field +
`invokeSuspend(result)` switching on it); resuming = `continuation.resumeWith(result)`.

But the **current CLR ABI** (`coroutine-abi-decision`, `coroutine-il`) is **Task-based**: `suspend fun f(): T`
→ `Task<T> f()` (a CLR `IAsyncStateMachine`), `await` via the Task awaiter, **Continuation hidden**. This is
great for .NET interop (a suspend fun is just a `Task<T>`, callable from C#), but it does NOT expose the raw
Continuation primitives kotlinx.coroutines compiles against. So the two are at odds.

### Trade-off
| | direct .NET interop | can compile kotlinx.coroutines |
|---|---|---|
| Task-based (current) | excellent (suspend fun == Task<T>) | **no** (no raw Continuation) |
| Continuation-based (standard) | poor (suspend fun == fun(Continuation), needs a bridge) | **yes** |

## 3. Proposed resolution: two layers

- **Layer 1 — Continuation core.** Adopt the STANDARD Kotlin suspend lowering: `suspend` → `fun(Continuation)` +
  a state-machine class implementing `kotlin.coroutines.Continuation`, with `COROUTINE_SUSPENDED` and the
  `suspendCoroutineUninterceptedOrReturn` intrinsic. This is the foundation kotlinx.coroutines (and `sequence{}`,
  see §6) compile against. `kotlin.coroutines.*` (Continuation, CoroutineContext, intrinsics) come from the
  kotlin-stdlib we already compile against; the compiler must implement their lowering, not reimplement them.
- **Layer 2 — Task bridge (interop).** Preserve .NET ergonomics with a thin bridge library (the CLR `actual`s /
  a `DotKt.Coroutines` helper): `suspend fun <T> Task<T>.await(): T` (already have it as `@ClrAwait`) and
  `fun <T> future(block: suspend () -> T): Task<T>` (run a coroutine to a Task) — the CLR analog of
  kotlinx-coroutines' JDK8 `future {}` / `await`. For exposing a Kotlin `suspend fun` to C# AS a `Task<T>`, a
  small opt-in wrapper (annotation-driven, like `@JvmAsync`) can generate a Task-returning shim.

Net: the standard model becomes the core (so libraries compile); Task interop becomes a (thin, real) library on
top, not the ABI itself. **This supersedes the Task-based ABI as the primary lowering** — a breaking change,
which is exactly why we publish 0.9.0 pre-1.0 (`dotkt-3package-distribution`).

## 4. Compiler work to support Layer 1
- The standard suspend state-machine lowering (Continuation, label, invokeSuspend, COROUTINE_SUSPENDED). The
  existing CPS front-end (`emitCps`, flat steps) is reusable conceptually; the *lowered form* changes from
  Task/IAsyncStateMachine to Continuation/state-machine-class.
- **suspend-lambda CPS** (#55's other half): currently only trivial `{ f() }` lambdas work (`lambda()` uses plain
  `stmt()`); kotlinx.coroutines is suspend-lambda-heavy, so non-trivial suspend lambdas must CPS-linearize like
  suspend funs.
- The coroutine intrinsics (`suspendCoroutineUninterceptedOrReturn`, `createCoroutineUnintercepted`,
  `startCoroutine`, `intercepted`) lowered to the state-machine primitives.

## 5. Library-build prerequisites (to compile kotlinx-coroutines-core)
- **expect/actual** (multiplatform) handling in the compiler driver (compile commonMain + a CLR actual set).
- **atomicfu** — kotlinx.coroutines depends on it; map `kotlinx.atomicfu.*` to `System.Threading.Interlocked` /
  `Volatile` (an actual set, or recognize the atomicfu intrinsics).
- **CLR actual source set**: `CoroutineDispatcher` actuals — `Dispatchers.Default`/`IO` → `ThreadPool` /
  `TaskScheduler`, `Main` → a `SynchronizationContext`; the event loop for `runBlocking`; time source for `delay`.
- Then `Flow`/`Channel`/builders are commonMain Kotlin compiled as-is.

## 6. #42 (sequence builder) folds in here
`sequence { yield(x) }` / `generateSequence` are **kotlin-stdlib** (not kotlinx), but `sequence{}` is a
**restricted-suspension coroutine** (`SequenceScope`, `@RestrictsSuspension`, suspend `yield`). So it rides on the
SAME Continuation foundation. Once Layer 1 exists, `sequence`/`generateSequence` largely **compile from stdlib**
(or a tiny `SequenceScope` actual) instead of being hand-built in DotKt.Runtime. So #42 is not a separate
reimplementation — it's unblocked by, and folded into, the Continuation core. (The earlier note in
`dotkt-naming-and-runtime-split` — "sequence builder → DotKt.Runtime stateful iterator" — is reframed: build it
from stdlib on the Continuation primitives.)

## 7. Open design gates (decide before coding)
1. **Confirm the two-layer ABI** (Continuation core + Task bridge) and that the Task-based primary ABI is retired.
2. Continuation representation on CLR: `kotlin.coroutines.Continuation<T>` as a real interface implemented by the
   state-machine class; `resumeWith` dispatch. Reuse vs. replace the existing IAsyncStateMachine emitter (ilemit).
3. How to expose a Kotlin `suspend fun` to C# as `Task<T>` (opt-in wrapper / annotation) — keep .NET interop good.
4. atomicfu strategy (actual set vs intrinsic recognition).
5. Driver multiplatform: real HMPP (common + CLR actuals) vs. a flat "common + actuals as one module" compile.

## 8. Phasing (once gates are agreed)
1. Continuation-core lowering + the coroutine intrinsics (replace/extend the Task state machine). Keep a Task
   bridge so existing coroutine samples still pass.
2. suspend-lambda CPS.
3. expect/actual + atomicfu + a minimal CLR actual set; compile a small slice of kotlinx-coroutines-core.
4. Full kotlinx-coroutines-core (Flow/Channel); fold in `sequence`/`generateSequence` (#42).

---

# Refinements (2026-06-21 design discussion)

## 9. Arity is the organizing axis

The confusion dissolves once you split by **cardinality**, not by "Kotlin vs .NET". Each side has a single-shot
and a multi-shot member, and they pair up cleanly:

| arity | Kotlin | .NET (cold / backpressured) | .NET (hot / push) | callback primitive |
|---|---|---|---|---|
| 0..1 (single) | `suspend fun` / `Deferred<T>` | `Task<T>` | — | **`Continuation<T>`** |
| 0..N (multi)  | `Flow<T>` | **`IAsyncEnumerable<T>`** | `IObservable<T>` (Rx) | `FlowCollector` / `IObserver` |

- **Single-shot:** `Continuation` and `Task` are the SAME arity, which is exactly why they integrate trivially —
  `Task` ≈ a reified, started `Continuation`-completion. Bridge: `await` = `suspendCoroutine { c ->
  task.GetAwaiter().OnCompleted { c.resumeWith(...) } }`; `future{}` = a `TaskCompletionSource` resumed by the
  coroutine's `Continuation`. (Literally kotlinx-coroutines-jdk8's `future`/`await`.)
- **Multi-shot:** `Flow` is COLD + suspend-based (backpressure = the collector suspends). Its closest .NET analog
  is **`IAsyncEnumerable<T>`** (cold, `await MoveNextAsync`, backpressured) — NOT Rx `IObservable` (hot, push, no
  backpressure, which needs an impedance layer). `flow{emit}` ⟷ `async IAsyncEnumerable`+`yield return`;
  `flow.collect{}` ⟷ `await foreach`. Rx interop stays opt-in.
- `Continuation` is structurally a *single-shot Observer* (`resumeWith` = `OnNext`+`OnCompleted` once); `Observable`
  is its N-shot generalization — so "Observable is the general form" is true, but it belongs to the Flow LAYER, not
  the single-shot core. Don't unify the core on Observable (over-general: every single-shot would carry a 0..1
  constraint). Unify single on `Continuation`/`Task`, multi on `Flow`/`IAsyncEnumerable`.

## 10. The ABI delta is "generalize", not "rewrite"

The current `suspend fun → Task<T>` (CLR `IAsyncStateMachine`) is ALREADY a continuation-passing state machine:
at a suspension point, `builder.AwaitUnsafeOnCompleted(awaiter, ref sm)` registers `sm.MoveNext` as the awaiter's
**continuation**. The CPS front-end (`emitCps`, flat steps, await spilling) — the genuinely hard part — is done.
The only thing hardwired is the **completion sink**: `AsyncTaskMethodBuilder` (completes a `Task`). Generalize
that sink and the standard model appears: `Task` = "a `Continuation` whose sink is a `TaskCompletionSource`".

So the work is INCREMENTAL, and **`Task` is NOT retired — it stays as the default sink / one impl of `Continuation`**,
so existing Task-based coroutine code keeps working alongside the exposed Continuation form. This shrinks the
breaking surface (0.9.0 remains the prudent version, but 1.0 may not be strongly blocked by it). Remaining delta:
1. ilemit: a `Continuation`-driven state-machine form (completion sink pluggable: Task default OR a supplied `Continuation`).
2. expose the intrinsics: `kotlin.coroutines.Continuation`, `COROUTINE_SUSPENDED`, `suspendCoroutineUninterceptedOrReturn`.
3. **suspend-lambda CPS** (extend `emitCps` to lambda bodies; today only trivial `{ f() }` works — see `lambda()`).
4. dispatcher mapping: `CoroutineDispatcher` → `SynchronizationContext` / `TaskScheduler`.

## 11. Two product goals (decide LATER, not now)

- **A — Kotlin-flavored structured async on Task.** Hand-write a thin scope library on the existing `suspend→Task`:
  `CoroutineScope`/`Job` ≈ `CancellationTokenSource` + child-Task tracking, `launch`/`async` spawn child Tasks,
  `coroutineScope{}` ≈ `Task.WhenAll(children)`, cancellation propagates. NO ABI change. Not the literal kotlinx
  library (no exact semantics / Flow operator set), but covers most structured-async needs.
- **B — compile the real `kotlinx.coroutines`.** §1–§5. Full API, the Continuation ABI work + library-build prereqs.

Crucially the two share the SAME .NET mapping (§9, §12): A hand-writes it, B compiles kotlinx and maps its
primitives to the same `Task`/`IAsyncEnumerable`/scheduler targets. So the choice can be deferred — it does not
change the foundation.

## 12. Scope decomposition (not two equal scopes)

- The **Task-version scope** (`CoroutineScope`/`Job`) is the real structured-concurrency engine (children +
  `CancellationToken`, `WhenAll`, cancel propagation).
- The **`IAsyncEnumerator`-version "scope"** (a `Flow` collection) is mostly just the cold `IAsyncEnumerable`
  correspondence with NO scope of its own (1 `collect` = 1 `IAsyncEnumerator`, its lifetime = the iterator frame,
  dispose = cancel). Only the CONCURRENT bits of Flow (`channelFlow`/`produce`/`flatMapMerge`/buffer) need a scope —
  and they **delegate to the Task scope** (launch a child producer that writes to a `System.Threading.Channels`
  channel). So it is **one real scope (Task) + the `Flow = IAsyncEnumerable` correspondence**, with multi-value
  concurrency borrowing the single-value scope — same shape for A and B.

## 13a. DECISION (2026-06-21, user-confirmed) — Path B, B2-as-generalization

**Goal locked: compile the real upstream `kotlinx-coroutines-core` into an assembly `dotktx.coroutines`.**
User rationale: *"being able to build upstream IS the proof of compatibility."* (1.0 ship-gating is out of scope
for this effort.)

**Architecture = B2-as-generalization** (own the CPS end-to-end, generalize the completion *sink*). Rejected B1
(insert `AbstractSuspendFunctionsLowering`/`JsSuspendFunctionsLowering`): `buildStateMachine` is abstract so the
CPS core is ours regardless; B1 would force consuming a relooped goto-free IR we don't need and pull in the whole
`backend.common` lowering-context the pipeline deliberately avoids (no IR lowerings run between Fir2Ir and
`ClrBackendPhase`). B3's "two engines" dissolves into **one engine, two sinks**.

**ABI (user-confirmed): internally suspend-centric / Continuation-universal; CLR-facing appearance stays
`Task<T>`.** Every `suspend fun`/suspend lambda lowers to the standard Continuation form (state-machine class
implementing `kotlin.coroutines.Continuation<T>`, `label` + `invokeSuspend`/`resumeWith`, value-or-
`COROUTINE_SUSPENDED`). The *default public surface* of a `suspend fun` remains a `Task<T>`-returning kickoff =
`future { internalForm }`, so `coroutine-abi-decision` (suspend fun == Task<T>) is **preserved as the surface**;
only the internal lowering gains Continuation. Reconciling insight (user): **"Continuation can be regarded as
Task"** — the boundary continuation's sink IS a `TaskCompletionSource` (Task ≈ a reified, started
Continuation-completion; same single-shot arity, §9). The existing struct+`AsyncTaskMethodBuilder` path is
**refactored into the Task sink**, not deleted.

**Resolutions:** (1) `future{}`=root Continuation→TCS; `await(Task)`=`suspendCancellableCoroutine`. (2)
Continuation = real CLR interface implemented by emitted SM classes. (3) Dispatchers: Default/IO→ThreadPool,
Main→SynchronizationContext (WPF/WinUI), runBlocking→blocking event loop, delay→event-loop timer. (4)
expect/actual = pragmatic minimum: commonMain + a CLR `actual` set compiled as ONE flat module (no HMPP/klib).
(5) atomicfu = recognize intrinsics in the backend → Interlocked/Volatile. (6) Flow⟷IAsyncEnumerable,
Channel⟷System.Threading.Channels (cold/backpressured, not Rx); most of Flow compiles as-is.

**Status:** Phase 0 ✅ DONE (2026-06-21). Non-trivial suspend lambdas (capturing & non-capturing, loops/
multi-suspension) now CPS-linearize: `BirEmitter.lambda` routes them through the shared `emitCoroutineBody`
(extracted from `suspendMethod`) so the lifted method / closure `invoke` carries `suspend`+steps+cpsFields and
`ilemit.EmitCoroutine` emits the SM + `Task<T>` kickoff. Capturing lambdas → the closure `invoke` is an INSTANCE
coroutine: ilemit captures the receiver into an SM field `<>4__this` (`_coThis`) so resume reaches captured-var
fields. Proof: `cases/il-colam` (30/6/105/18) in `scripts/verify-il.sh`; full IL suite green.

**Phasing (each = a verifiable gate):** 0 suspend-lambda CPS → 1 Continuation core + pluggable sink → 2 raw
intrinsics → 3 flat expect/actual + atomicfu → 4 first real slice (Job/launch/async/Dispatchers/delay/
withContext/coroutineScope/runBlocking) → 5 full commonMain (Flow/Channel) = headline compat gate → 6
sequence{} fold-in (#42) + UI dispatcher. Full plan: `~/.claude/plans/eager-tinkering-scroll.md`.

## 13b. Phase 1 locked design (2026-06-21, PoC-proven)

> **SUPERSEDED (runtime home).** The "ship in a runtime DLL" decision below is replaced by
> [coroutine-stdlib-port-plan.md](coroutine-stdlib-port-plan.md): the Continuation runtime moves into the stdlib
> `clr/` actuals (`DotKt.Runtime` being eliminated). IN-FLIGHT. The *shapes* (Result/Continuation/Context/sink) below remain accurate.

**Continuation runtime = shared types in the `DotKt.Coroutines` NAMESPACE** (originally a separate `DotKt.Coroutines.dll`;
folded into `DotKt.Runtime.dll` 2026-06-23, see §14 — and split by Kotlin namespace per §14a). The backend maps the
`kotlin.coroutines.*` / `kotlin.Result` fqnames onto its types (like `kotlin.Throwable`→`System.Exception` and
the `DotKt.Fmt` promotion), NOT per-assembly synthesis (`<>dotkt_Result` would break cross-assembly identity —
the user assembly and `dotktx.coroutines` must share one `Continuation`). Criterion: cross-assembly identity →
DLL (`dotkt-naming-and-runtime-split`). This is the conscious introduction of a coroutine runtime DLL that the
pure-BCL Task ABI didn't need.

Shapes (PoC-proven, `/tmp/coro-poc`, `chain=30`/`runBlocking=30` with a cross-thread resume + Task bridge):
- `Result<T>` struct (Success/Failure/IsFailure/ExceptionOrNull/GetOrThrow).
- `CoroutineContext` interface + `EmptyCoroutineContext.Instance` (Element/Key/dispatcher later).
- `Continuation<T>` — **INVARIANT** on CLR (JVM erases `in T`; invariance is the CLR-safe choice): `Context`
  getter + `ResumeWith(Result<T>)`.
- `Intrinsics.COROUTINE_SUSPENDED` (reference-identity sentinel).
- `Builders.Future<T>(ctx, start)` [Task sink — the default public surface] / `RunBlocking<T>(start)`.

Generated state machine = a **class implementing `Continuation<T>`**: fields `_label`/`_param`(resume value)/
`_err`/`_completion`(parent Continuation) + cps fields; `Context => _completion.Context`; `ResumeWith` unpacks
the Result, calls `InvokeSuspend`, returns on `COROUTINE_SUSPENDED` else `_completion.ResumeWith(Success(v))`
(catch → `Failure(e)`); `InvokeSuspend` is the label switch (reuses the existing `coSuspend`/`coLabel`/`coGoto`/
`coReturn` step stream) where a suspension point returns the sentinel if it suspends.

**Phase boundary refinement:** the leaf intrinsic `suspendCoroutineUninterceptedOrReturn` **folds into Phase 1**
(was Phase 2) — the Continuation form is only exercisable by intrinsic-using code, so they are one natural,
verifiable unit. Phase 2 then = the rest (`startCoroutine`/`createCoroutineUnintercepted`/`intercepted` +
`suspendCancellableCoroutine` on top).

**Status: Phase 1 ✅ DONE (2026-06-22).** The Continuation-class codegen + shared runtime + Task sink are
implemented and ilverify-clean. `ilemit.EmitCoroutineClass`/`EmitCoSuspendClass` emit a class implementing
`DotKt.Coroutines.Continuation<object>` (`ResumeWith`→`InvokeSuspend` label switch; `COROUTINE_SUSPENDED`
sentinel; cps fields + `<>4__this` capture; try/catch-around-await), with the kickoff binding a `NewRoot<T>`
Task sink. Selected by `"coClass":true` (backend `@KCont` opt-in; struct/Task form stays default). Proof:
`cases/il-kcont` (30/14/6/15/10/-99) in `scripts/verify-il.sh`, ilverify clean. NOTE: the leaf suspension is
currently realized via the existing `.await()` → `Builders.AwaitOnto` (a Task awaiter registering `ResumeWith`),
NOT yet the raw `suspendCoroutineUninterceptedOrReturn` — that frontend intrinsic (+ `kotlin.Result` mapping +
handing the SM out as a typed `Continuation`) is **Phase 2**, and is what lets a coroutine hand its continuation
to arbitrary Kotlin code (required to compile upstream).

## 13c. Phase 2 status (2026-06-22) — raw intrinsics

**Phase 2 ✅ (core) DONE.** The raw leaf intrinsic `suspendCoroutineUninterceptedOrReturn { c -> ... }` is
recognized (`BirEmitter.emitSuspendIntrinsic`): the block is inlined with `c` bound to the coroutine's OWN
continuation (`coSelfCont` = `new TypedCont<T>(this)`), its leading statements run, and its result drives a
`coSuspendIntrinsic` step where ilemit does the value-or-`COROUTINE_SUSPENDED` branch (`EmitCoSuspendIntrinsicClass`;
state set BEFORE the block runs so a same-thread resume during registration is safe). Mapped: `kotlin.coroutines.
Continuation<T>` → `clrg:DotKt.Coroutines.Continuation` (birType+netType), `COROUTINE_SUSPENDED` →
`coSuspendedSentinel`, `resume`/`resumeWithException` → `DotKt.Coroutines.Continuations.*`. Runtime gained
`TypedCont<T>` (the reified-T adapter), `Continuations`, `Builders.OnComplete`/`OnCompleteInt`. Proof:
`cases/il-kintrin` (7/42/72 — real suspension via the intrinsic, sync-return leaf, composition), ilverify-clean.
Deferred (arrive when compiling upstream): generic suspend funs (generic SM class), `kotlin.Result` mapping,
`startCoroutine`/`createCoroutineUnintercepted`/`intercepted`, `suspendCancellableCoroutine` (compiles on top).

## 13d. Phase 3 status (2026-06-22) — driver expect/actual + atomicfu

**Phase 3 ✅ DONE.** (a) **expect/actual**: `Main.kt` sets `arguments.multiPlatform = true`; the common fragment is
designated per-invocation via the standard `-Xcommon-sources=<files>` CLI flag (a flat one-module compile is
REJECTED by the frontend — expect & actual must be different fragments). commonMain + a CLR `actual` set thus
compile in ONE invocation; expects emit no `.bir.json`, actuals do. No HMPP/klib. (b) **atomicfu**: `kotlinx.
atomicfu.{AtomicInt,AtomicLong,AtomicBoolean,AtomicRef}` map (birType/netType) to `DotKt.Coroutines.Atomic*`
Interlocked/Volatile wrappers (`runtime/DotKt.Runtime/Atomics.cs (now namespace DotKtx.Atomicfu)`); `BirEmitter.atomicfuCall` maps the
`atomic(x)` factory (by arg type) + member ops (`.value`, `compareAndSet`, `incrementAndGet`, `addAndGet`, …).
Proof: `cases/il-expect` (expect/actual + AtomicInt + AtomicRef), ilverify-clean, in `verify-il.sh`
(`il_check_mpp`). NOTE: atomicfu wrappers are the correct "thin actual set" (not the JVM field-erasure) — revisit
for perf only if needed. The real `kotlinx-atomicfu` jar isn't on the test classpath; a facade with matching
fqNames stands in (the backend maps identically for the real jar).

## 13e. Phase 4+ status (2026-06-22) — the wall to literal-upstream compilation

**Foundation (Phases 0–3) is DONE and verified.** Phases 4–6 = compiling the LITERAL upstream
`kotlinx-coroutines-core` sources, which is a large multi-step effort beyond the foundation. Concrete blockers
identified (in priority order), each a bounded compiler feature:

1. **Generic suspend functions / generic state machines** (DEMONSTRATED blocker). `@KCont suspend fun <T>` fails
   in `ilemit` (`unresolved generic type parameter T` — the kickoff `Task<T>` signature, then the SM type, must
   carry the method's generic params). Upstream is pervasively generic (`suspendCancellableCoroutine<T>`,
   `Deferred<T>.await()`, `withContext<T>`, `Flow<T>`), so this is the #1 prerequisite. Needs: define generic
   params on the kickoff; make the SM a generic type over them; thread through Continuation<object> boundary.
2. **Default Continuation form** — upstream code isn't `@KCont`-annotated. Once linked with the runtime, ALL
   suspend funs need the class form by default (the struct/Task form can't hand out a `Continuation`). Flip the
   default (or auto-select when a fun uses the raw intrinsics / is library code).
3. **`kotlin.Result` mapping** → `DotKt.Coroutines.Result` (upstream calls `resumeWith(Result)` directly, not
   only the `resume`/`resumeWithException` extensions already mapped).
4. **`startCoroutine` / `createCoroutineUnintercepted` / `intercepted`** + `CoroutineContext` element/key/
   `ContinuationInterceptor` — needed for `launch`/`async`/dispatchers.
5. **CLR actual source set** for the library: `CoroutineDispatcher` actuals (Default/IO→ThreadPool, Main→
   SynchronizationContext), the `runBlocking` event loop, `delay` time source, `CancellableContinuation`.
6. **Breadth**: full kotlin-stdlib call coverage across the real sources; `JobSupport` (atomicfu-heavy + intricate
   state machine); `Channel`→`System.Threading.Channels`; `Flow`→`IAsyncEnumerable`; `select`. This is the bulk.

**Single-shot SEQUENTIAL semantics already work** on the foundation (`cases/il-kintrin`: intrinsic suspension +
composition `chainViaIntrinsic=72`). CONCURRENT structured concurrency (launch/async/cancellation) needs items
2–5; literal upstream needs all of 1–6. Recommended next increment: **(1) generic suspend-fun state machines**,
then **(4)+(5)** a minimal dispatcher/scope, then attempt a hand-picked upstream slice.

## 13f. Phase 4a status (2026-06-22) — generic suspend functions ✅ DONE

Blocker #1 cleared. `suspend fun <T>` now lowers to a **generic state-machine TYPE** over the fun's type params
(class form; BirEmitter routes all generic suspend funs to `coClass`). `DeclareMethod` defines the kickoff's
generic params (so `Task<T>`/param types resolve); `EmitCoroutineClass` defines matching SM generic params, sets
`_curTypeParams` to them while emitting the SM bodies, and — per the Reflection.Emit rule — routes the SM's own
field/method self-references through the self-instantiation `sm<ownParams>` (`SelfF`/`TypeBuilder.GetMethod`),
while the kickoff resolves against `sm<methodT>` (`SmField`/`SmCtor`). Generic-param values box via `box T` and
unbox via `unbox.any T` (NOT castclass — the value/ref distinction is only known at runtime). Also: `emitCps`
now unwraps a type-operator wrapper around a suspension (generic substitution / coercion-to-Unit). Proof:
`cases/il-kgen` (`awaitTwice`/`second` with `T=Int` and `T=String` → 7/hi/2/b), ilverify-clean, in verify-il.sh.
Remaining for literal upstream: blockers #2–#6 (§13e).

## 13g. Phase 4b status (2026-06-22) — CPS fixes + Unit-result class coroutines

Three correctness items cleared on the way to structured concurrency:
- **Nested-lambda CPS isolation**: `containsSuspend`/`spillExpr` no longer descend into nested lambdas/local funs
  — a suspension inside an inner `async{…}` lambda is that inner coroutine's, not the enclosing one's (it was
  being mis-spilled into the outer SM).
- **Generic facade method shapes**: `clrMethodShape` now matches ilemit's `Shape()` — parameterized generics
  (`Task<T>`, `Continuation<T>`) → `"generic"`, function types → `"func:N"` (bare param stays `"gp"`, array `"array"`).
  This makes generic `@Clr` method calls over generic-typed params resolve.
- **Unit-result class coroutines**: `suspend fun … : Unit` in the class form surfaces as a non-generic `Task`
  via a new `Builders.RootUnit`/`NewRootUnit` sink. Proof: `cases/il-kunit` (a Unit `warmUp` awaited by
  `useUnit` → 42), ilverify-clean.

**Structured-concurrency demo ✅ LANDED** (`cases/il-kstruct`, ilverify-clean): `async` starts concurrent
children, `await` (a suspend extension fun built from the raw intrinsic) joins them, `runBlocking` drives the
root — `runBlocking { val a=async{fetch…}; val b=async{fetch…}; a.await()+b.await() } == 30` (concurrent) and a
sequential `== 42`. The fix was **extension suspend funs**: `suspendMethod` now prepends the extension receiver
as a `__self` param + cps field (mirroring `method()`) and binds `<this>`→`__self`, so the receiver is captured
into the state machine instead of mis-resolving to the SM. (This also resolved the trivial-forward-with-receiver
and struct-lambda-builder issues, which were downstream of the receiver not being a real arg.) Flow operators are
extension suspend funs, so this is a key upstream enabler.

**Remaining for literal upstream commonMain (Phase 5+)**: the breadth — full kotlin-stdlib call coverage across
the real sources, `JobSupport` (atomicfu-heavy), `Channel`→`System.Threading.Channels`, `Flow`→`IAsyncEnumerable`,
`select`; plus the Continuation form as the DEFAULT (upstream isn't `@KCont`-annotated) and `kotlin.Result` /
`startCoroutine` / `intercepted` / dispatcher actuals. The single-shot core (suspend funs, generic + Unit,
intrinsics, structured async on the Task sink) now works end-to-end.

## 13h. Phase 6 status (2026-06-22) — `sequence { yield }` builder ✅ DONE (#42)

The restricted-suspension (multi-shot) coroutine builder works: `sequence { yield(…) }` → a lazy .NET
`IEnumerable<T>`. The block's `yield(v)` calls CPS-linearize to **`coYield`** steps; ilemit emits a state machine
implementing the trivial `DotKt.Coroutines.ISeqStep<T>` (`MoveNext` advances to the next yield and sets
`Current`, returning true; resume after each yield; end → false), and `DotKt.Coroutines.Seq.Of` (a C# iterator)
wraps it into `IEnumerable<T>` — keeping the awkward `IEnumerator<T>` dual-interface boilerplate in C#, not IL.
`kotlin.sequences.Sequence<T>` maps to `IEnumerable<T>` (ops ride the existing LINQ mapping). The SM is emitted
inline at the call site (enclosing emit state saved/restored). Proof: `cases/il-kseq` — straight-line yields
(1,2,3), yield-in-loop with a live local (1,4,9,16), and an **infinite** `sequence{ while(true) yield(i++) }`
`.take(2)` (0,1 — proves laziness; doesn't hang), ilverify-clean. v1: non-capturing blocks (loud error otherwise);
`yieldAll`/`generateSequence` not yet. Restricted-suspension thus rides the same CPS front as suspend funs (§6).

## 13i. Phase 5 (slice) status (2026-06-22) — Flow works on the foundation

**Flow needs NO new state-machine form** — it is push-based, so it composes from plain suspend funs/lambdas on
the Task foundation (the user's intuition). A `Flow` wraps a `suspend (collector) -> …` block; `collect` runs the
block with a collector whose `emit` is the consumer action; `emit` awaits the action's Task (backpressure = the
producer suspends until the collector returns). `cases/il-kflow`: `flow { emit(1); emit(2); emit(3) }.collect
{ println(it) }` → 1/2/3, ilverify-clean. Runtime `DotKt.Coroutines/Flows.cs` (FlowColI/FlowI/Flows, Int slice);
`emit`/`collect` are suspend extension funs built from the raw intrinsic (like the structured-async `await`).
Two enabling compiler fixes (both general): birType maps function TYPES used as values (`kotlin.FunctionN` /
`kotlin.coroutines.SuspendFunctionN` → `Func<…,Task<R>>`); and **`capturedVars` no longer treats a nested
lambda's own parameters as captures of the enclosing lambda** (it now adds nested `IrValueParameter`s to the
declared set) — this was a latent capture bug that the Flow nesting (`flow{ col -> }` / `collect{ v -> }`) exposed.

**Remaining for full Phase 5 (literal upstream commonMain)**: generic Flow<T>/Channel/select/SharedFlow/StateFlow,
`Flow`↔`IAsyncEnumerable` boundary bridge, `JobSupport` (atomicfu-heavy), and the full stdlib breadth across the
real sources — still the large asymptote. But the Flow CORE mechanism is proven to ride the foundation.

## 13j. A1/A2 done; A3/B/C are the coupled literal-upstream effort (2026-06-22)

**A1 ✅** auto-route intrinsic-using suspend funs to the Continuation class form (no `@KCont`; `isCoClass` =
@KCont OR generic OR body-uses-`suspendCoroutineUninterceptedOrReturn`). Proven by dropping @KCont from
il-kintrin/kstruct/kflow.
**A2 ✅ (resume side)** `resume`/`resumeWithException`/`resumeWith(Result.success|failure)` → `DotKt.Coroutines.
Continuations.{Resume,ResumeWithException}<T>` (generic-static, MakeGenericMethod). Sample il-kresume (5, 107).
Also fixed capValueExpr/closure-teardown to honor captureSubst.

**Why A3/B/C don't separate cleanly (the honest structure):**
- The general `kotlin.Result` (runCatching / `getOrNull(): T?`) is best kept as the per-assembly field-inlined
  `<>dotkt_Result` — `getOrNull` returns `Nullable<T>` for value T and value-or-null for ref T, resolved
  per-type at the call site. Unifying it with the coroutine `DotKt.Coroutines.Result` would break that `T?`
  semantics. So the two Result representations should stay separate.
- Consequence: a USER implementing `Continuation<T>.resumeWith(Result<T>)` in Kotlin needs the result param to
  be `DotKt.Coroutines.Result` — only relevant when compiling upstream's internal Continuation impls
  (CancellableContinuationImpl, DispatchedContinuation). Our hand-written structured layer never implements
  Continuation in Kotlin (the SM is compiler-generated; completions are runtime C# Root/RootUnit), so it doesn't
  need this.
- Therefore **A3 (`startCoroutine`/`createCoroutineUnintercepted`/`intercepted`), B (dispatcher actuals), and C
  (Flow/Channel/JobSupport breadth) are mutually coupled AND only meaningfully testable against real upstream
  sources** — they are the literal-upstream-compilation asymptote, not independently-shippable standalone steps.

**Net:** every coroutine mechanism (single-shot suspend funs incl. generic/Unit/extension, raw intrinsics +
resume API, structured async/await/runBlocking, multi-shot sync `sequence{}`, multi-shot async push `Flow`) is
proven on the foundation. What remains is bringing in the literal upstream library (its internal Continuation
impls, dispatchers, JobSupport, the full Flow/Channel operator set) — quantity on a proven foundation.

## 13k. Generic Flow<T> done — a real compiler feature, not "quantity" (2026-06-22)

Correction to §13j's framing: generic `Flow<T>` was NOT just upstream quantity — it needed two genuine compiler
fixes (the monomorphic-Int Flow slice had masked them):
1. **Member access on a generic type instantiated with an emitted generic parameter** (`FlowCol<T>.emitRaw`,
   `Deferred<T>.task`): runtime reflection (`GetProperty`/`GetMethod`) refuses a `TypeBuilderInstantiation`, so
   ilemit re-anchors the open definition's member onto the constructed type via `TypeBuilder.GetMethod`
   (`PropAccessor`; `EmitClrCall` fallback). This was the kstruct blocker that forced the monomorphic pivot.
2. **Suspend-call return type**: a call to a `suspend fun` resolves to its kickoff returning `Task<T>`; the
   `retType` hint (used by ilemit on generic calls) must be `coTaskType`, not the result `T`, else an awaited
   GENERIC suspend call is typed as the result and `GetAwaiter` isn't found (`retHintStr`/`effRet`).
Proof: `cases/il-kgflow` — `flow<String> { emit("a"); emit("bb"); emit("ccc") }.collect { println(it.length) }`
→ 1/2/3, ilverify-clean. So generic facade types + generic suspend funs + generic-instantiated member access all
work now. (The genuinely-upstream-coupled remainder is still A3/B/C per §13j — but the generic-Flow machinery is
a finished compiler feature.)

## 13. The single concrete next step

Across §10/§11/§12, every scope/producer body is a `suspend` lambda (`launch{…}`, `flow{…}`, `runBlocking{ multi
statements }`). So the one irreducible, design-independent task is **suspend-lambda CPS** — extend `emitCps` from
suspend funs to lambda bodies. It unblocks both paths and both arities; the big ABI/A-vs-B questions can wait until
that real code exists. **Recommended first implementation step for #55.**

## 13l. T1 done — startCoroutine (2026-06-22)

`(suspend ()->T).startCoroutine(completion)` and the receiver overload `(suspend R.()->T).startCoroutine(receiver,
completion)` now lower (`BirEmitter` call() early-dispatch) to `DotKt.Coroutines.Builders.StartCoroutine<T>` /
`StartCoroutineR<R,T>`: run the block's kickoff Task and route its outcome into the supplied `Continuation<T>`
(normal→`Continuations.Resume`, throw→`ResumeWithException`). The completion's `T` comes from the
`startCoroutine<T>` **call type argument** (NOT the completion arg's declared type — it may be a concrete
`Continuation<T>` implementor like the runtime `CaptureI`, whose own type args aren't `T`; mis-reading it as
`object` passed `Continuation<Int>` where `Continuation<object>` was expected → an EntryPointNotFound on the
generic-interface dispatch, which looked like a deep CLR bug but was just the wrong type arg). Generic path works;
no monomorphic special-case needed. Proof: `cases/il-kstart` (a suspending `produce` started into a runtime
`CaptureI` sink → 42), ilverify-clean. `createCoroutine`/`createCoroutineUnintercepted` (non-starting forms) not
yet — add when upstream needs them.

## 13m. T2 done — suspendCancellableCoroutine (2026-06-22)

`kotlinx.coroutines.suspendCancellableCoroutine { c -> … }` recognized (shares `emitSuspendIntrinsic` with the raw
intrinsic, `alwaysSuspend=true`): the block always suspends (returns Unit, not the sentinel), and `c` is a
`CancellableContinuation<T>` = `coSelfCancellable` → `new CancellableCont<T>(new TypedCont<T>(this))`.
`runtime/DotKt.Runtime` gained `CancellableCont<T> : Continuation<T>` (forwards resume; cancel/
invokeOnCancellation minimal — real cancellation lands with the dispatcher). `c.resume(v)` rides the existing
`kotlin.coroutines.resume` mapping. Type map: `kotlinx.coroutines.CancellableContinuation` → `CancellableCont`.
Proof: `cases/il-kcancel` (`awaitC` via suspendCancellableCoroutine, composed → 30), ilverify-clean.

## 13n. T4 done — kotlin.Result unified onto DotKt.Coroutines.Result (2026-06-22)

Retired the per-assembly synthetic `<>dotkt_Result`; `kotlin.Result<T>` now maps (birType/netType) to the shared
`DotKt.Coroutines.Result<T>` struct — ONE cross-assembly type, so it serves both `runCatching` AND (next) the
`Continuation.resumeWith` parameter. Changes: `runCatching` builds via `Result.Success/Failure`; the method
accessors (getOrNull/getOrThrow/getOrDefault/exceptionOrNull) inline in `call()` over `IsSuccess`/`Value`/
`ExceptionOrNull`; the property getters `isSuccess`/`isFailure` arrive two ways and both are mapped — directly as
**`IrGetField`** (kotlin.Result is an inline `value class`) and as a getter `IrCall` reaching the generic
property path (stdlib getter bodies absent → not a custom accessor); plus a standalone `Result.success/failure`
value mapping. `il-result` green, full suite green.

## 13o. T5 done — a user Kotlin class implements Continuation<T> (2026-06-22)

A user Kotlin `class C : Continuation<Int>` does NOT yet emit: (a) the class supertype lists the interface as the
bare `Continuation` not `clrg:DotKt.Coroutines.Continuation[int]` (the class-supertype path uses ownerSpec, not
the .NET mapping — needs a Continuation special-case there), and (b) deeper — the Kotlin override `resumeWith`/
`context` (camelCase) must bind to the .NET interface's `ResumeWith`/`Context` (PascalCase). So: a user class
implementing a .NET-mapped interface needs Kotlin→.NET member-name mapping in the override emission. This is the
remaining T5 work (T4 unblocked the Result param type; this casing piece is separate).

## 13o-done. T5 resolved (2026-06-22)

A user `class C : Continuation<Int>` now compiles and runs (cases/il-kcont2 → 42 / boom), closing the §13j gap
(user Continuation impls). What it took, all general "Kotlin class implements a .NET-mapped interface" machinery:
- class supertype list maps kotlin.coroutines.Continuation -> `clrg:DotKt.Coroutines.Continuation[int]` (was bare).
- Kotlin members bind to the .NET PascalCase slots via `clrIfaceMemberName`: `resumeWith`->`ResumeWith`,
  `context` getter -> `get_Context`; applied at method/accessor emission AND the call site; the accessor is emitted
  even though it `override`s (isCustomAccessor excludes overrides).
- type maps: `kotlin.coroutines.CoroutineContext`/`EmptyCoroutineContext` -> `DotKt.Coroutines.CoroutineContext`;
  the `EmptyCoroutineContext` object value -> a `clrStaticField` load of `.Instance` (new ilemit expr kind).
- ilemit: the interface-impl site, the interface-linking (DefineMethodOverride by .NET name via reflection), and
  the type-ordering Visit all learned to resolve `clr:`/`clrg:` interfaces by reflection (not `_types`).
Result<T> param works because T4 already unified kotlin.Result -> the shared struct.

## 13p. T11 done — suspend receiver lambdas (2026-06-22)

The idiomatic `flow { emit(x) }` form now works (sample il-kflow2 → 1/2/3): a `suspend FlowCol<T>.() -> Int`
lambda whose body calls a suspend EXTENSION (`emit`) on the implicit receiver. Fix: `emitCoroutineBody` now
includes a suspend lambda's extension receiver as a leading param/field (it was using only `regularParams`, so the
implicit `$this$flow` was an unknown var). EXCLUDES the `kotlin.sequences.SequenceScope` receiver — `sequence{}` is
the restricted-suspension builder whose scope IS the state machine (synthetic, not a passed value), lowered by the
sequence-special path; including it broke il-kseq. `sequence { yield(x) }` already proved receiver-style for the
member-call case; this adds the extension-on-receiver case. Full suite green.

## 13q. T6 (yieldAll) done — generateSequence pending (2026-06-22)

`SequenceScope.yieldAll(elements)` works (il-kseq → 0,1,2,3,4,5,6, incl. a nested `yieldAll(sequence{…})`): a new
`coYieldAll` step lowers in the sequence SM to an inner enumerator loop — get the Iterable/Sequence's
IEnumerable<elem> enumerator into a per-step SM field ONCE (the resume dispatch jumps past the init), then each
MoveNext advances it (current=Current; state=k; return true) until exhausted. Also fixed: `emitCoroutineBody` now
SAVES/RESTORES the CPS state (coState/coLabelN/coSpill*/coFields) — a `sequence{}` nested inside a `yieldAll` reset
the outer's state ids, causing duplicate resume labels. `generateSequence` is deferred: its `nextFunction: (T)->T?`
hits the same nullable-in-generics issue as T7 (value-type T? = Nullable<T> vs ref-type T-or-null) — fold it into T7.

## 13r. T7 (Unit as a type argument) done — generateSequence's nullable-T still pending (2026-06-22)

Unit as a generic TYPE ARGUMENT now works: `Continuation<Unit>` / `Result<Unit>` (il-kunit2 → true). A CLR generic
arg can't be `System.Void`, so a `Unit` type argument erases to the new `DotKt.Coroutines.Unit` singleton (in
return/statement position Unit still lowers to `void` — those sites are guarded by `isUnit()` BEFORE birType/
netType, so this only affects the previously-broken type-arg path). `firstArgBir`/`firstArgNet` helpers map a Unit
first-arg to `clr:DotKt.Coroutines.Unit`, used by the Continuation/CancellableCont/Result maps + the standalone
`Result.success/failure` value; the `Unit` object value -> `clrStaticField DotKt.Coroutines.Unit.Instance`.
STILL PENDING (the other half of the old T6/T7 conflation): `generateSequence`'s `nextFunction: (T) -> T?` — the
nullable-in-generics representation (value-type `T?` = `Nullable<T>` vs reference `T`-or-null) is a DISTINCT problem
from Unit-as-arg and isn't addressed here.

## 13s. T8 (Channel) done — Flow↔IAsyncEnumerable bridge still pending (2026-06-22)

`kotlinx.coroutines.Channel<T>` maps to `DotKt.Coroutines.Chan<T>` over `System.Threading.Channels` (il-kchan →
6): suspend `send`/`receive` await the Task forms (`Writer.WriteAsync(...).AsTask()` / `Reader.ReadAsync().AsTask()`);
capacity<=0 → unbounded. A bounded channel lets a single coroutine send N then receive N (no dispatcher needed).
Enabling fix (general): `receive<T>(): T` is the first raw-intrinsic leaf whose result is the method's own type
param T, so `coSelfCont` builds `new TypedCont<T>(this)` with T an EMITTED generic param — reflection can't resolve
the ctor on that instantiation. Added `CtorOf` (re-anchor via `TypeBuilder.GetConstructor` when a type arg is a
TypeBuilder/generic-param), used by coSelfCont/coSelfCancellable. (kgflow's emit<T> returned the Int dummy, so its
TypedCont<Int> was concrete and dodged this.) STILL PENDING in T8: the `Flow`↔`IAsyncEnumerable` bridge; full
Channel close/iteration/fan-out semantics (needs dispatchers — Track 2).

## 13t. T8 complete — Flow <-> IAsyncEnumerable bridge (2026-06-22)

`GFlows.FromAsync<T>(IAsyncEnumerable<T>) : Flow<T>` (asFlow) drains a .NET async stream, emitting each element to
the collector (await = backpressure); `GFlows.ToAsync<T>(Flow<T>) : IAsyncEnumerable<T>` (asAsyncEnumerable) runs
the flow into an unbounded Channel and exposes the reader as an async stream (push→pull). Sample il-kasflow:
`Flows.fromAsync(Kfc.Api.range(4)).collect { println(it*10) }` → 0/10/20/30 (a C# `async IAsyncEnumerable<int>`
bridged into a Kotlin Flow). T8 (Channel + Flow↔IAsyncEnumerable) done; full suite green.

## 13u. generateSequence done — T6 complete (2026-06-22)

`generateSequence(seed?, next)` and `generateSequence(next)` lower to `DotKt.Coroutines.Seq.Generate{Ref,Val}` /
`Generate{Ref,Val}N` (il-kgenseq → 1,2,3,4,5 / a,aa,aaa / 1,2,3). The nullable-in-generics issue (`(T) -> T?` is
`Func<T, Nullable<T>>` for a value T vs `Func<T, T>` for a reference T) is solved by the COMPILER picking the
value- vs reference-variant from T's kind at the call site (T is known) — two helpers, one chosen, instead of one
impossible unified signature. Lazy (take short-circuits the infinite seedless form). With yieldAll (§13q), T6 is
done. (The same value/ref-split trick is the general answer for nullable-T-in-generics elsewhere.)

## 13v. T10 (finally-around-await) done — suspension-in-catch / catch+finally still loud (2026-06-22)

`try { …suspend… } finally { … }` now works (il-kfinally → cleanup / 15, with TWO suspensions inside the try and
the finally running exactly once after, never on a suspend). The mechanism: a `finally` around a suspension is NOT
emitted as a CLR finally clause — a suspend `leave`s the .try to return from MoveNext, which would run a real
finally on every suspend. Instead `emitTryCps` carries the finally steps on `coTryEnd`, and ilemit's `EmitCoTryEnd`
(shared by struct + class SM forms) emits them on the normal-exit path AND in a synthesized `catch(Exception){
<finally>; rethrow }`. v1 scope: fall-through try body (a `return` inside the try skips the finally — known gap);
finally only with NO catch clauses; a suspending finally / catch is a loud error.
Observed but ORTHOGONAL: the finally also runs on the exception-unwind path (cleanup printed before the throw), but
a DIRECT throw in a struct-form coroutine body isn't routed to its Task (escapes MoveNext) — a separate pre-existing
exception-routing gap, not T10. Remaining hard tail: T9 select, T3 CoroutineContext Key<E> algebra + intercepted.

## 13w. T9 (select) done — NO new compiler machinery (2026-06-22)

`select { onAwait(t) { … } }` works (il-kselect → 2000: two clauses race, the 10ms one beats the 80ms one, its
`v*1000` handler wins). KEY FINDING: select needed ZERO compiler changes — modeled as a runtime `Selector<R>`
(collects (Task, suspend-handler) clauses) + `Selectors.Select` (awaits `Task.WhenAny`, runs the winner's handler),
it composes purely from already-working features: the `select { … }` block is a receiver lambda (T11) registering
clauses via a member call, the handlers are suspend lambdas, plus generics + await. Only friction: a
`Selector<R>.() -> Unit` receiver lambda is emitted as `Func`2` ("func:2"), so the runtime block param is
`Func<Selector<R>, int>` (Int-result dummy, the Flow convention), not `Action`1`. (This is purely a CLR-native
select over Task.WhenAny; real kotlinx select's exact clause DSL — `SelectClause1`/atomic registration — would come
with literal upstream, Track 2.) Track-1 standalone compiler features are now ALL done; only T3 (CoroutineContext
Key<E> algebra + intercepted) remains, and it belongs with Track 2 (dispatchers / real upstream).

## 13x. T3(a) done — coroutineContext; T3 is Track-2-INDEPENDENT (2026-06-22)

CORRECTION (user, 2026-06-22): T3 is NOT gated on building upstream — dispatchers are a CLR **actual set**
buildable standalone NOW; "needs dispatchers" ≠ "needs upstream" (the same conflation corrected earlier). T3 is
needed regardless and proceeds independently, Track-1-style (synthetic samples).
T3(a): `kotlin.coroutines.coroutineContext` (the suspend top-level property) -> the SM's own Context (the class-form
SM IS a `Continuation<object>`, so `coContext` = `ldarg.0; callvirt get_Context`). Accessing it forces the
Continuation-class form (added to the isCoClass body scan). il-kctx → 1 (`coroutineContext === EmptyCoroutineContext`,
the default). Next T3 increments: (b) the `CoroutineContext` element algebra (Element/Key/get/plus/fold/minusKey/
CombinedContext); (c) `ContinuationInterceptor`/`intercepted` + a minimal `CoroutineDispatcher`.

## 13y. T3(b) — CoroutineContext element algebra (2026-06-23)

Runtime: the kotlin stdlib `CoroutineContext` algebra mirrored in C# — `get(key)`/`fold`/`plus`/`minusKey`,
`Element`/`Key<E>`/`IKey`, `AbstractElement`, `EmptyCoroutineContext`, `CombinedContext`, `Contexts.Plus` (the
fold-based merge). Compiler: the Kotlin members map to the .NET PascalCase methods — `plus`/`minusKey` (non-generic,
`clrInstance`), `fold<R>` (generic instance, `clrGenericInstance`, op = `Func<R,Element,R>` "func:3"); `get<E>` is
next. Type maps: `CoroutineContext.Element` → `clr:DotKt.Coroutines.Element`, `.Key` → `clrg:…Key[…]`. il-kctx → 15
(`coroutineContext.fold(10){…}` = 10 over the empty context, `c.plus(c)` identity → +5). Remaining T3: a real
Element-with-value + `get<E>(Key)` (user/runtime element); then (c) `ContinuationInterceptor`/`intercepted` + a
minimal `CoroutineDispatcher`.

## 13z. T3(c) done — ContinuationInterceptor / intercepted + dispatcher; T3 COMPLETE (2026-06-23)

`intercepted()` (kotlin.coroutines.intrinsics.intercepted) -> `Interceptors.Intercepted<T>(cont)`: look up the
`ContinuationInterceptor` in the continuation's own context (by key) and apply it. Runtime: `ContinuationInterceptor
: Element` with `InterceptContinuation<T>`, `Interceptors.Key`/`Intercepted`, + a synthetic `RecordingDispatcher`
(wraps the continuation so resume bumps a recorder then forwards) and a `SinkI` whose context carries it.
il-kintercept -> 1 / 7 (intercepted finds the dispatcher, wraps; resume dispatches through it then to the sink).
**T3 COMPLETE** (a coroutineContext, b context element algebra, c interceptor/intercepted+dispatcher) — and with it
ALL of Track-1. A real `Dispatchers.Default`/`Main` (ThreadPool / SynchronizationContext) is just a richer
`ContinuationInterceptor` on this exact seam — that lands with Track 2's CLR actual source set.

## 14. Runtime assembly refactor — DotKt.Coroutines.dll folded into DotKt.Runtime.dll (2026-06-23)

> **SUPERSEDED (runtime home).** "All ship in DotKt.Runtime.dll" is no longer the target. Per
> [coroutine-stdlib-port-plan.md](coroutine-stdlib-port-plan.md) these `kotlin.coroutines.*` / `kotlin.sequences.*`
> types move into the pure-Kotlin stdlib `clr/` actuals and `runtime/DotKt.Runtime/Coroutines.cs` is deleted — IN-FLIGHT
> (that file still exists). The `kotlinx.*` stopgaps still graduate to `dotktx.coroutines` (Track 2).

Per the namespace=boundary principle (`kotlin.*` → DotKt.Runtime; `kotlinx.*` → its own dotktx.* assembly), the
stray `DotKt.Coroutines.dll` ASSEMBLY (it held `kotlin.coroutines` lowerings — Continuation/CoroutineContext/Result/
Unit/intrinsics/intercepted + the sequence/Flow/Channel/select stopgap helpers) is folded into `DotKt.Runtime.dll`.
The `DotKt.Coroutines` NAMESPACE is unchanged, so the compiler's type maps (`clr:DotKt.Coroutines.X`) need NO change
— this was purely a build/packaging move: the 7 .cs files moved into runtime/DotKt.Runtime/, the DotKt.Coroutines
project deleted, and verify-il.sh drops the separate DOTKT_CO build/ref/copy (the types resolve via DOTKT_RT, which
is now copied next to each emitted dll for the run). `DotKt.Coroutines.dll` was never packaged (pack-nuget only
packs DotKt.Runtime), so the distribution naturally gains the coroutine runtime. Full suite green, ilverify-clean.
(When the real kotlinx-coroutines-core is compiled — Track 2 — it ships as its own `dotktx.coroutines`, and the
stopgap kotlinx-ish helpers here are superseded.)

## 14a. Namespace hygiene — DotKt.Coroutines projects kotlin.coroutines ONLY (2026-06-23)

The runtime namespaces now mirror the Kotlin source namespaces (user principle: a DotKt namespace is the projection
of the corresponding Kotlin namespace):
- `DotKt`            ← `kotlin.*` root: `Result`, `Unit`.
- `DotKt.Coroutines` ← `kotlin.coroutines.*` ONLY: Continuation, CoroutineContext/Element/Key, Intrinsics
  (COROUTINE_SUSPENDED), Continuations (resume), TypedCont, ContinuationInterceptor, Interceptors (intercepted),
  Builders (the Task sink) + kotlin.coroutines test scaffolds (CaptureI/IntTag/RecordingDispatcher/SinkI).
- `DotKt.Sequences`  ← `kotlin.sequences.*`: Seq, ISeqStep (the sequence{}/generateSequence builder helpers).
- `DotKtx.Coroutines`← `kotlinx.coroutines.*` (stopgaps): CancellableCont, Chan, Flow*/GFlows, Selector(s),
  Structured/DeferredI.
- `DotKtx.Atomicfu`  ← `kotlinx.atomicfu.*`: AtomicInt/Long/Boolean/Ref.
All still ship in DotKt.Runtime.dll for now; the `DotKtx.*` (kotlinx) sets graduate to their own `dotktx.*`
assemblies when the real upstream is compiled (Track 2). Compiler type maps + sample @Clr facades updated to match;
full suite green, ilverify-clean.

> **SUPERSEDED (runtime home).** "All still ship in DotKt.Runtime.dll" describes the 2026-06-23 state only; the
> `kotlin.*` sets are being ported into the stdlib `clr/` actuals per [coroutine-stdlib-port-plan.md](coroutine-stdlib-port-plan.md) (IN-FLIGHT). The namespace-mirroring rationale is still the design intent.
