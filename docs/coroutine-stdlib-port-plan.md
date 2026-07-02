# Coroutine ABI port plan — move the CLR coroutine runtime into the stdlib

Status: **planning** (2026-06-28). Prereqs already landed this session:
- `DotKt.Runtime` removed from the stdlib build (`scripts/build-stdlib-ref.sh` no longer `--ref`s it).
- `kotlin.Result` / `kotlin.Unit` lowerings retired under `DOTKT_STDLIB_COMPILE` (stdlib uses its own types).
- ilemit's suspend/sequence emitter no longer hardcodes `DotKt.Coroutines.*` / `DotKt.Sequences.*`; the type names are
  centralized into one seam (the `Co*` consts in `toolchain/ilemit/Program.cs`) pointing at the canonical
  `kotlin.coroutines.*` / `kotlin.sequences.*` names the stdlib provides.

Background / decisions this plan builds on: [coroutine-abi.md](coroutine-abi.md) (the `suspend ⇔ Task<T>` contract),
[coroutine-il.md](coroutine-il.md) (strategy B = CLR-native async), [design-coroutines-clr.md](design-coroutines-clr.md)
(two-layer Continuation/Task model). This doc is the *migration* plan, not new ABI design — the ABI is settled:
**a `suspend fun` is `Task<T>` on the CLR surface; the Continuation core is the internal lowered form.**

This plan **SUPERSEDES the runtime-home decision in [design-coroutines-clr.md](design-coroutines-clr.md) §14 / §14a**
("all ship in DotKt.Runtime.dll"): the `kotlin.coroutines.*` / `kotlin.sequences.*` runtime moves into the stdlib
`clr/` actuals and `DotKt.Runtime` is eliminated. Naming note: `@Clr` is the current `kotlin.clr.ClrIntrinsic`; and the
Kotlin↔CLR suspend lowering is a **bir2cir-layer** concern (ilemit just emits the resolved CLR form) — the older
BirEmitter→ilemit framing predates that split.

## 0. Goal

The pure-Kotlin `DotKt.Stdlib` is the runtime. It must **self-provide every CLR coroutine type** an emitted `suspend
fun` binds against, with **no reference to `DotKt.Runtime`**. End state: `runtime/DotKt.Runtime/Coroutines.cs` is
deleted; the stdlib emits and a `suspend fun` runs as `Task<T>`.

## 1. Current state — what already exists vs. what must be ported

The stdlib ALREADY defines the full Kotlin-facing `kotlin.coroutines.*` API (in `runtime/stdlib/src/kotlin/coroutines/`
+ `clr/` actuals):
- `Continuation<in T>` (`resumeWith`), `RestrictsSuspension`, `SafeContinuation` (clr actual), `ContinuationInterceptor`.
- `createCoroutine` / `startCoroutine` / `suspendCoroutine` starters.
- `CoroutineContext`, `Element`, `Key`, `AbstractCoroutineContextElement`, `AbstractCoroutineContextKey`,
  `EmptyCoroutineContext`, `CombinedContext`.
- `kotlin.coroutines.intrinsics`: `COROUTINE_SUSPENDED` (a top-level `val` over the internal `CoroutineSingletons`
  enum), `suspendCoroutineUninterceptedOrReturn`, `createCoroutineUnintercepted`, `startCoroutineUninterceptedOrReturn`.

`runtime/DotKt.Runtime/Coroutines.cs` is a **parallel C# implementation** of those same Kotlin-facing types (a
duplicate) PLUS a few helpers that have **no Kotlin equivalent** — these are the genuinely CLR-specific bridge, and the
ONLY things that must actually be *ported* (the rest the stdlib already covers in Kotlin):

| C# helper (DotKt.Coroutines) | role | needed by |
|---|---|---|
| `TypedCont<T>` | adapt the object-typed SM to a typed `Continuation<T>` for `suspendCoroutineUninterceptedOrReturn` | DEFAULT path (`coSelfCont` intrinsic) |
| `Intrinsics.COROUTINE_SUSPENDED` (static field) | the suspension sentinel (compared by `===`) | DEFAULT path (`coSuspendedSentinel`) |
| `Builders.AwaitOnto` / `OnComplete` | the `await(Task)` leaf: resume a continuation when a Task completes | `await`/Task-bridging suspend funs |
| `Builders.Future` / `Root<T>` / `RootUnit` / `NewRoot` | the Task sink: drive a Continuation-class SM into a `TaskCompletionSource<T>` | OPT-IN `@KCont` path only |
| `Builders.StartCoroutine` / `RunBlocking` | start a `suspend ()->T` into a completion / block for it | `startCoroutine`, `runBlocking` |

Note the **default** `suspend fun` path (`EmitCoroutine` → `struct SM : IAsyncStateMachine` + `AsyncTaskMethodBuilder` →
`Task<T>`, `toolchain/ilemit/Emitter.Coroutines.cs`) is **already pure BCL** for the Task machinery. It only needs
`TypedCont` + `COROUTINE_SUSPENDED` + the context types from the bridge. `Builders/Root` are used only by the opt-in
`@KCont` Continuation-class SM path.

## 2. The single seam

`toolchain/ilemit/Program.cs` — the `Co*` consts are the one place type names are decided:
```
CoContinuation = "kotlin.coroutines.Continuation`1"      (stdlib HAS it)
CoContext      = "kotlin.coroutines.CoroutineContext"    (stdlib HAS it)
CoEmptyContext = "kotlin.coroutines.EmptyCoroutineContext"(stdlib HAS it)
CoIntrinsics   = "kotlin.coroutines.intrinsics.IntrinsicsKt"  (COROUTINE_SUSPENDED holder — see G2)
CoTypedCont    = "kotlin.coroutines.TypedCont`1"         (PORT)
CoBuilders     = "kotlin.coroutines.Builders"            (PORT)
CoCancellableCont = "kotlinx.coroutines.CancellableCont`1" (dotktx — out of scope, see G5)
CoSeqStep      = "kotlin.sequences.ISeqStep`1"           (PORT — sequence builder)
CoSeq          = "kotlin.sequences.Seq"                  (PORT — sequence builder)
```

## 3. Design gates (decide before coding — recommendations below)

- **G1 — where the ported bridge types live.** Recommend: `internal` Kotlin declarations in the stdlib `clr/` actuals,
  fqnames matching the seam consts exactly. `kotlin.coroutines.TypedCont`, `kotlin.coroutines.Builders` (new file
  `runtime/stdlib/clr/kotlin/coroutines/CoroutineBridgeClr.kt`); `kotlin.sequences.Seq` / `ISeqStep` (new
  `runtime/stdlib/clr/kotlin/sequences/SequenceBridgeClr.kt`). They are CLR-platform-only, so they belong in `clr/`,
  not `common`/`src`. (Kotlin upstream keeps platform-internal coroutine impl in `kotlin.coroutines.jvm.internal`; the
  "jvm" name is wrong on CLR, so a flat internal `kotlin.coroutines` declaration is cleaner. Final names are whatever
  the seam consts say — change in ONE place.)
- **G2 — `COROUTINE_SUSPENDED` shape.** The stdlib exposes it as a top-level `val` getter, not a static field; ilemit
  currently does `GetField`. Recommend: emit `coSuspendedSentinel` as a CALL to the getter
  (`kotlin.coroutines.intrinsics.IntrinsicsKt.get_COROUTINE_SUSPENDED()`), which returns the stable
  `CoroutineSingletons.COROUTINE_SUSPENDED` singleton. (Alternative: have the bridge expose a real static field; the
  getter is less churn.) The `===` identity check the SM does still holds because the enum value is a singleton.
- **G3 — ilemit type resolution during stdlib-compile (the key mechanical unblock).** `ResolveType("kotlin.coroutines.
  Continuation`1")` must find the type when it is being EMITTED (it lives in ilemit's `_types`, not a loaded assembly).
  Recommend: extend `ResolveType` to consult `_types` (the in-flight `TypeBuilder`s) for `kotlin.*` names, falling back
  to loaded assemblies. App builds already resolve from the referenced `DotKt.Stdlib.dll`. (This is what the current
  blocker `cannot resolve .NET type kotlin.coroutines.Continuation`1` needs.)
- **G4 — the `@KCont` Continuation-class path.** It's opt-in (`coClass:true`) and the only consumer of `Builders.Root`.
  Recommend: port `Builders` anyway (small, and `startCoroutine`/`runBlocking` use it), but treat `@KCont` itself as
  optional — the default `Task`/`AsyncTaskMethodBuilder` path covers ordinary `suspend fun`s. Decide whether to keep or
  retire `@KCont` once the default path is green.
- **G5 — `kotlinx.coroutines.CancellableCont`.** Out of scope: it is kotlinx, not kotlin stdlib. The stdlib build never
  reaches it. Leave the seam const as a placeholder; provide it later in `dotktx.coroutines` ([[dotktx-coroutines-path-b]]).
- **G6 — Kotlin↔BCL Task binding.** The Task sink (`Root<T>` wrapping `TaskCompletionSource<T>`, `AwaitOnto` using
  `GetAwaiter().OnCompleted`) is the only part needing real BCL interop. Port it as `@Clr`-bound Kotlin (the
  `stackalloc`/`Span` and event-interop precedents show BCL binding from Kotlin works). This is the main implementation
  risk — generics + `TaskCompletionSource` + the `OnCompleted` callback closure.

## 4. Phasing (each phase ends green / re-emittable)

0. **Lock G1–G6** (this doc). Pick final bridge fqnames; update the seam consts to match.
1. **ilemit G3**: resolve `kotlin.*` coroutine types from `_types` during stdlib-compile. Unblocks the current abort;
   the remaining bridge types (TypedCont/Builders/Seq) will then be the next "cannot resolve" — that names the exact
   port surface.
2. **Port `TypedCont` + G2 sentinel**: `CoroutineBridgeClr.kt` with `internal class TypedCont<T>(raw: Continuation<Any?>)
   : Continuation<T>`; switch `coSuspendedSentinel` to the getter. Re-emit: the DEFAULT `suspend fun` path should now
   resolve everything it needs.
3. **Port `Builders`** (Root/RootUnit/Future/NewRoot/AwaitOnto/OnComplete/StartCoroutine/RunBlocking) as `@Clr`-bound
   Kotlin over `TaskCompletionSource`/`Task`. Covers `await`, `startCoroutine`, `runBlocking`, and the `@KCont` path.
4. **Port the sequence builder** (`Seq` / `ISeqStep`) into `kotlin.sequences` clr actuals for `sequence { }`.
5. **End-to-end verify**: stdlib emits; a sample `suspend fun` returns `Task<T>` and runs (resurrect/adapt the il-coro /
   il-kresume / il-kcancel cases against the stdlib-provided runtime instead of DotKt.Runtime).
6. **Retire**: delete `runtime/DotKt.Runtime/Coroutines.cs` (and `Interceptor.cs` if subsumed); confirm nothing else
   references `DotKt.Coroutines`. Decide G4 (@KCont keep/retire).

## 5. Files

- Touch: `toolchain/ilemit/Program.cs` (G3 resolution + final seam const names), `toolchain/ilemit/Emitter.Expressions.cs`
  (G2 sentinel getter).
- Create: `runtime/stdlib/clr/kotlin/coroutines/CoroutineBridgeClr.kt` (TypedCont, Builders),
  `runtime/stdlib/clr/kotlin/sequences/SequenceBridgeClr.kt` (Seq, ISeqStep).
- Delete (phase 6): `runtime/DotKt.Runtime/Coroutines.cs`.

## 6. Risks

- **G6 BCL interop from Kotlin** (TaskCompletionSource generics + OnCompleted closure) is the hard part. Fallback if
  `@Clr`-binding a generic `TaskCompletionSource<T>` from Kotlin proves too fiddly: keep `Builders` as a tiny C# helper
  compiled INTO `DotKt.Stdlib` (not a separate DotKt.Runtime) — still satisfies "stdlib is self-contained".
- The DEFAULT path already being pure-BCL means phases 1–2 alone likely make ordinary `suspend fun`s emit; phases 3–4
  are for `await`/`runBlocking`/`@KCont`/`sequence{}`. Sequence builders may surface the broken break/continue-in-
  coroutine control-flow item (separately tracked: control-flow lowering → CIR).
- Cross-assembly identity: an app's compiled `suspend fun` and `DotKt.Stdlib` must bind the SAME `Continuation`/`Result`
  types. Since both now resolve `kotlin.coroutines.Continuation` from the one `DotKt.Stdlib.dll`, identity holds (this is
  exactly why the duplicate C# types had to go).
