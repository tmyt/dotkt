# Design groundwork: coroutines on CLR / building kotlinx.coroutines

Status: **design-first, not yet implemented** (task #55). This file locks the problem shape and the proposed
two-layer ABI before any code. See memory `dotkt-compile-kotlin-libraries`, `coroutine-abi-decision`.

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

## 13. The single concrete next step

Across §10/§11/§12, every scope/producer body is a `suspend` lambda (`launch{…}`, `flow{…}`, `runBlocking{ multi
statements }`). So the one irreducible, design-independent task is **suspend-lambda CPS** — extend `emitCps` from
suspend funs to lambda bodies. It unblocks both paths and both arities; the big ABI/A-vs-B questions can wait until
that real code exists. **Recommended first implementation step for #55.**
