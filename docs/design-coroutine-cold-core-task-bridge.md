# Coroutine cold core + CLR Task bridge design note

Status: design note (2026-07-03). This refines the coroutine direction in
[coroutine-abi.md](coroutine-abi.md), [design-coroutines-clr.md](design-coroutines-clr.md), and
[coroutine-stdlib-port-plan.md](coroutine-stdlib-port-plan.md).

The short version:

- Kotlin-facing coroutine bodies should be cold, Continuation-based computations.
- CLR-facing `suspend fun` exports should remain hot `Task<T>` / `Task` methods.
- `.NET Task -> Kotlin suspend` interop should be a CLR platform extension (`Task.await`) supplied outside the
  frontend stdlib jar.
- `Task`, `Sequence`, `IAsyncEnumerable`, and future kotlinx builders should be adapters/sinks over a shared
  coroutine core, not the core representation itself.

## 1. Core distinction

There are two different meanings that must not be collapsed:

| Shape | Execution model | Role |
|---|---|---|
| `suspend () -> T` / internal suspend body | cold | Kotlin coroutine value/body |
| public CLR `Task<T> Foo(...)` | hot | .NET interop boundary |
| `Sequence<T>` / `IEnumerable<T>` | cold pull | synchronous multi-shot adapter |
| `Flow<T>` / `IAsyncEnumerable<T>` | cold async pull | asynchronous multi-shot adapter |
| `Task<T>` consumed by Kotlin | already hot | external .NET computation awaited from Kotlin |

The shared core should be a standard Kotlin-style coroutine:

```text
f$dotkt_suspend(args..., completion: Continuation<T>): Any?
```

It returns either:

- a value of type `T` (or `Unit`) when it completes synchronously;
- `COROUTINE_SUSPENDED` when it suspends;
- an exception by throwing, or by resuming the completion with failure from an async callback.

## 2. `suspend fun` emits two callable shapes

For:

```kotlin
suspend fun f(x: Int): String
```

emit two conceptual methods.

### Kotlin/coroutine body

```text
f$dotkt_suspend(x: Int, completion: Continuation<String>): Any?
```

This is the real coroutine body. Kotlin-to-Kotlin suspend calls target this shape directly. They should not allocate a
`Task`, and should not go through the public CLR method.

### CLR public bridge

```text
F(x: Int): Task<String>
```

This is the public CLR ABI promised by `coroutine-abi.md`: C# and F# callers see a normal hot `Task`.

The bridge is equivalent to `future { f$dotkt_suspend(...) }`, but codegen should use a direct bridge instead of
allocating a suspend lambda:

```text
public Task<T> F(args...) {
  tcs = new TaskCompletionSource<T>()
  root = new RootContinuation<T>(tcs)

  try {
    r = f$dotkt_suspend(args..., root)
    if (r !== COROUTINE_SUSPENDED) complete tcs with r
  } catch (e) {
    complete tcs with exception e
  }

  return tcs.Task
}
```

Later optimization can avoid `TaskCompletionSource<T>` on synchronous completion by returning `Task.FromResult`,
`Task.CompletedTask`, or `Task.FromException`. The semantic model is still a root continuation whose sink is a Task.

## 3. Kotlin should not await its own public `Task` wrapper

Within Kotlin-generated code, a suspend call should lower to:

```text
callee$dotkt_suspend(args..., callerContinuation)
```

not:

```text
callee(args...).await()
```

Going through `Task` would:

- allocate an unnecessary Task for every Kotlin suspend call;
- weaken direct `Continuation` / `CoroutineContext` / interceptor wiring;
- make `sequence`, `startCoroutine`, and kotlinx builders harder to align with the standard coroutine model;
- blur hot CLR interop with cold Kotlin coroutine values.

## 4. `.NET Task` consumed from Kotlin: `Task.await`

The reverse bridge is a Kotlin-facing suspend extension:

```kotlin
package kotlin.clr

public suspend fun Task.await(): Unit
public suspend fun <T> Task<T>.await(): T
```

This API is for Kotlin users on the CLR. C# already has `await task`, so the extension should not be treated as a C#
user-facing API.

Semantics:

```text
Task.await(task, continuation):
  awaiter = task.GetAwaiter()

  if awaiter.IsCompleted:
    return awaiter.GetResult()      // Unit for non-generic Task

  awaiter.OnCompleted(callback):
    try:
      result = awaiter.GetResult()
      continuation.resume(result or Unit)
    catch e:
      continuation.resumeWithException(e)

  return COROUTINE_SUSPENDED
```

Use `GetAwaiter().GetResult()` rather than `Task.Result` so exception behavior follows normal .NET await semantics.

CancellationToken / Job integration is a later layer. The minimal bridge should first support plain Task completion and
fault propagation.

## 5. Where `Task.await` lives

`Task.await` should not be compiled into the frontend stdlib jar. The jar should remain the pure Kotlin stdlib surface
and should not contain `System.Threading.Tasks.Task`.

Instead:

```text
libraries/stdlib/clr/.../Task.kt
  CLR-platform source containing Task.await declarations/implementation or declarations that bind to helpers.

facadegen
  Injects Kotlin metadata for kotlin.clr.await(Task) and kotlin.clr.await(Task<T>) into the frontend-visible CLR
  platform metadata.

bir2cir
  Consumes the marker / intrinsic metadata for Task.await and lowers it to the TaskAwaiter + Continuation bridge.

stdlib.ref.dll / stdlib.rt.dll
  Contain the CLR-facing declaration and runtime/helper body as needed.
```

This keeps the split:

```text
stdlib jar:
  kotlin.* pure frontend symbols

facadegen metadata:
  .NET symbols + CLR platform extensions

bir2cir:
  consumes CLR platform markers and performs lowering/substitution
```

The extension should likely require an explicit import initially:

```kotlin
import kotlin.clr.await
```

Auto-import can be considered later.

## 6. Relation to `sequence` and async streams

`sequence {}` is a coroutine, but not a Task coroutine. It is a cold pull adapter:

```text
sequence {}
  -> Sequence / IEnumerable
  -> MoveNext drives the coroutine to the next yield
```

The real Kotlin stdlib implements this through `SequenceBuilderIterator`, which is both the `SequenceScope` receiver
and the completion `Continuation<Unit>`. `yield(value)` stores the value, saves the current continuation, and returns
`COROUTINE_SUSPENDED`.

That means DotKt should not infer "this receiver scope means `IEnumerable<T>`" as a general rule. The better model is:

```text
shared coroutine core
  + Task bridge sink
  + Iterator sink
  + AsyncIterator / IAsyncEnumerable sink
  + kotlinx library builders
```

`IAsyncEnumerable<T>` should likewise be a cold async-pull sink/adapter, not a reason to make the coroutine core hot.
External CLR builders that produce `IAsyncEnumerable<T>` should be recognized by explicit metadata or known-builder
tables, not guessed solely from receiver scope names.

## 7. CLR delegate interop: suspend lambda <-> Task delegate

The same cold/hot conversion is needed at CLR delegate boundaries.

A Kotlin suspend lambda has the conceptual shape:

```text
(P1, ..., Pn, Continuation<T>) -> Any?
```

A CLR async-style delegate usually has the shape:

```text
Func<P1, ..., Pn, Task<T>>
```

These are not the same type. Passing a Kotlin suspend lambda to CLR code that expects a Task-returning delegate must
generate an adapter.

### Kotlin suspend lambda to CLR Task delegate

When the target type is a CLR delegate returning `Task` / `Task<T>`, adapt the cold suspend lambda to a hot Task per
delegate invocation:

```text
Func<P..., Task<T>> wrapper =
  (args...) => {
    tcs = new TaskCompletionSource<T>()
    root = new RootContinuation<T>(tcs)

    try {
      r = lambda$dotkt_suspend(args..., root)
      if (r !== COROUTINE_SUSPENDED) complete tcs with r
    } catch (e) {
      complete tcs with exception e
    }

    return tcs.Task
  }
```

Conceptually this is `Func { args -> future { lambda(args) } }`, but codegen should use a direct root-continuation
bridge.

Do not perform this conversion when the target type is a Kotlin suspend function type. Kotlin-to-Kotlin suspend lambda
flow should remain cold and Continuation-based.

### CLR Task delegate to Kotlin suspend lambda

The reverse adapter is also required when a CLR Task-returning delegate is used where Kotlin expects a suspend function
value:

```text
Func<P..., Task<T>> -> suspend (P...) -> T
```

The generated suspend lambda should:

```text
call delegate(args...) -> Task<T>
await returned Task via Task.await()
```

Conceptually:

```kotlin
suspend { args... ->
  clrDelegate(args...).await()
}
```

This depends on the `Task.await` bridge described above.

### Conversion rule

The rule is target-type driven:

| Source | Target | Conversion |
|---|---|---|
| Kotlin suspend lambda | Kotlin suspend function type | none; keep cold |
| Kotlin suspend lambda | CLR delegate returning `Task` / `Task<T>` | root continuation + TCS wrapper |
| CLR delegate returning `Task` / `Task<T>` | Kotlin suspend function type | call delegate then `Task.await()` |
| Kotlin suspend lambda | CLR non-Task delegate (`Action`, `Func<T>`) | not supported |

This means CLR round-trips require explicit coroutine/Task transformation at the boundary:

```text
Kotlin coroutine -> CLR Task delegate -> Kotlin coroutine
```

is not identity; it inserts a Task bridge in one direction and an await bridge in the other.

## 8. Layer responsibilities

Target layering:

```text
kotc:
  - produce BIR from Kotlin source
  - preserve suspend/restricted-suspend/call facts
  - do not emit CLR Task bridges
  - do not emit sequenceNew-style CLR sink nodes

facadegen:
  - expose .NET types to Kotlin metadata
  - inject CLR platform extensions such as kotlin.clr.await(Task)
  - attach marker/intrinsic metadata for bir2cir

bir2cir:
  - lower suspend declarations to cold continuation bodies
  - synthesize CLR public Task bridges for exported suspend funs
  - lower Task.await to TaskAwaiter + Continuation bridge
  - adapt suspend lambdas to CLR Task-returning delegates when target type requires it
  - adapt CLR Task-returning delegates to Kotlin suspend function values when target type requires it
  - choose known sinks/adapters for sequence/iterator/async-stream patterns

ilemit:
  - emit CIR to IL
  - know about state-machine/sink CIR shapes
  - not depend on Kotlin names like sequence or await
```

## 9. Implementation migration sketch

1. Introduce a CIR-level representation for the cold continuation body:

   ```text
   method(args..., Continuation<T>): object
   returns value or COROUTINE_SUSPENDED
   ```

2. Emit a public Task wrapper for suspend functions using a root continuation + `TaskCompletionSource<T>`.

3. Move Kotlin-to-Kotlin suspend calls to the internal continuation body, not the public Task wrapper.

4. Add CLR platform `Task.await` metadata through facadegen and lower it in bir2cir.

5. Add target-type driven adapters:

   ```text
   suspend lambda -> Func<..., Task<T>>
   Func<..., Task<T>> -> suspend lambda
   ```

6. Move `sequenceNew`-like frontend lowering out of kotc. Either:
   - compile the real stdlib `SequenceBuilderIterator` against the shared coroutine core, or
   - temporarily lower known `kotlin.sequences.sequence` / `iterator` builders in bir2cir to an iterator sink.

7. Keep existing `AsyncTaskMethodBuilder` codegen only as an implementation option for the Task bridge/sink, not as the
   universal coroutine representation.

## 10. Design invariants

- Public CLR ABI for `suspend fun` remains `Task<T>` / `Task`.
- Kotlin coroutine core is Continuation-based and cold until started by a caller/builder/bridge.
- Kotlin code should call internal continuation bodies for suspend calls.
- `Task.await` is a CLR platform extension, not a pure stdlib jar API.
- CLR delegate interop performs explicit conversion between Kotlin suspend function values and Task-returning delegates.
- `Sequence` and `IAsyncEnumerable` are cold multi-shot adapters, not Task-shaped coroutines.
- Builder/sink selection should be explicit: known stdlib/kotlinx builders or marker metadata, not receiver-name
  guessing.

## 11. Implementation contract (P0 lock, 2026-07-03 — the approved bundle-6 plan)

Locked decisions the implementation phases (P1-P6) build against. The full phased plan lives in the
approved plan file; `docs/master-task-inventory.md 【6】` mirrors it.

### Naming + shapes

```text
suspend fun f(x: Int): String        (in class C / top-level file class FKt)
  ── cold entry:  public static/instance  object f$dotkt_suspend(int x, Continuation<object> completion)
  ── SM class:    internal sealed  class <owner>_f$sm : kotlin.coroutines.clr.internal.ContinuationImpl
                    fields: int label; <spilled params/locals>; object $result-plumbing via base
                    method: object invokeSuspend(object result)   // label dispatch: label/brIf/goto
  ── public bridge: Task<string> f(int x)                          // [KotlinFunction(Suspend)] rides here
```

- **The erased completion signature is `Continuation<object>`** (the CLR instantiation
  `kotlin.coroutines.Continuation`1<object>`), everywhere — JVM-equivalent erasure. Rationale: CLR
  interface contravariance (`in T`) does not lift value types (`Continuation<object>` is NOT
  convertible to `Continuation<int>`), so a uniformly-erased signature + boxing at the boundaries is
  the only shape that composes for generic/value results. `invokeSuspend` is `(object) -> object`.
  Boxing of value results at resume boundaries is accepted v1 cost (same as Kotlin/JVM).
- `COROUTINE_SUSPENDED` = the stdlib's existing `kotlin.coroutines.intrinsics` sentinel (already a
  real emitted singleton; ilemit consts NOT used — the SM references it as ordinary CIR).
- The SM extends **`kotlin.coroutines.clr.internal.ContinuationImpl`** (new stdlib CLR-internal base,
  ported from kotlin.coroutines.jvm.internal): `BaseContinuationImpl.resumeWith` drives the
  invokeSuspend loop + completion chaining + exception capture; `SuspendLambda` adds the
  create/invoke protocol for suspend lambdas. These are plain (non-suspend) Kotlin classes.

### Cross-assembly cold-call ABI

- The cold entry is a PUBLIC method named `<kotlinName>$dotkt_suspend`, emitted next to the bridge
  (same owner type; file-class for top-level). The name convention IS the linkage — no extra
  attribute. A consumer resolves it from the callee assembly via the already-scanned
  `MemberBinding.Suspend` flag + the convention (bir2cir rewrites the call site).
- The BRIDGE keeps the Kotlin-visible name (`f`) and the `Task<T>` signature — the C#-facing ABI and
  the `[KotlinFunction(Suspend)]` carrier (round-trip restore unchanged: kcc consumers see
  `suspend fun f(x: Int): String`; their suspend CALLS lower to the cold entry, non-suspend contexts
  and C# use the bridge).

### The kotlin.clr surface (names locked)

```kotlin
package kotlin.clr
public suspend fun <T> Task<T>.await(): T      // bir2cir-lowered: awaiter fast path / OnCompleted resume
public suspend fun Task0.await()               // non-generic System.Threading.Tasks.Task
public fun <T> blockOn(block: suspend () -> T): T   // start on the cold core + drain (the runBlocking analog)
public suspend fun delay(ms: Long)             // Task.Delay(ms).await()
```

(`Task`/`Task0` = the stdlib alias classes binding `System.Threading.Tasks.Task`1`/`Task`.)

### v1 limits (policy = call-time NotSupportedException, never an emit crash)

- No suspension inside `catch`/`finally` blocks (try/catch AROUND suspension works).
- No `suspendCancellableCoroutine` (kotlinx — purged). Plain `suspendCoroutine` works (stdlib source
  over the real intrinsics + SafeContinuation).
- No CancellationToken/Job/interceptor dispatch (later layers); `intercepted()` = identity v1.

### Supersession notes

- `coroutine-il.md`'s strategy-B (`IAsyncStateMachine`/`AsyncTaskMethodBuilder`) SM framing is
  SUPERSEDED: the SM is `ContinuationImpl`-based plain CIR; the ATMB machinery is deleted with
  `Emitter.Coroutines.cs` in P6. The hot-Task PUBLIC ABI (`coroutine-abi.md` §1) is unchanged.
- `coroutine-stdlib-port-plan.md`'s TypedCont/Builders port is DEAD (the class-form bridge types are
  never ported; the TCS RootContinuation replaces them).

## 12. Status + refinements (P0-P2 landed, 2026-07-03)

### Landed
- **P0** design-lock (§11). **P1** stdlib cold-core (`kotlin.coroutines.clr.internal` bases + RootContinuation
  + `kotlin.clr` await/blockOn/delay + Task/TCS aliases) — Task-facing files isolated in the jar-EXCLUDED
  `libraries/stdlib/clr/taskinterop/` source set (K2JVMCompiler can't resolve CLR names; §5). **P1b** kotlinx
  PURGED (breaking). **P2** bir2cir `SuspendColdLowering` v1: straight-line non-generic static top-level
  `suspend fun` → cold-entry `f$dotkt_suspend` + plain-CIR SM class (`ContinuationImpl` subclass) + synthesized
  draining `fun main`. Gate GREEN. Every other suspend shape is LEFT UNTOUCHED (keeps `"suspend":true` for the
  existing ilemit throw-stub → zero regression, rt-stdlib no-op via the app-build gate).

### The public Task bridge moved to P4 (P2 scope call, accepted)
P2 delivered the cold entry + SM + main-drain but NOT the public `Task<T>` bridge (deliverables c/d): the rungs
don't need it (suspend `main` drains; Kotlin→Kotlin suspend calls hit the cold entry directly), and the bridge
needs the TCS/Task CIR shapes that pair naturally with P4's `Task.await` interop + the real `blockOn` drain. So
the bridge + the ilemit `suspendBridge` stamp land in **P4**, alongside await.

### P4 symbol-surfacing mechanism (user, 2026-07-03) — kotc cares about ZERO coroutine symbols
Do NOT hand-care kotlin.clr coroutine symbols in kotc. Split by whether the SIGNATURE is CLR-free:
- **blockOn / delay → `expect`/`actual`.** CLR-free signatures → `expect` in `libraries/stdlib/common/src`
  (jar-INCLUDED → the frontend resolves `import kotlin.clr.blockOn` from the classpath, kotc untouched). Two
  actuals across the two builds (jar and ref/rt are separate K2 compilations; the jar already runs
  `-Xmulti-platform -Xcommon-sources -Xexpect-actual-classes`): a staged **jar-side stub actual**
  (`= throw UnsupportedOperationException()`; the jar is a never-executed frontend classpath — EXACT precedent =
  the `JvmNameActual.kt`/`JvmInlineActual.kt` staging for the `@OptionalExpectation` JvmName/JvmInline expects),
  and the **real CLR actual** in `taskinterop/` (Monitor drain / `Task.Delay().await()`), ref/rt only.
- **await → facadegen injection** (this section §5). `suspend fun Task<T>.await(): T` names Task → not an
  expect/actual candidate. facadegen, surfacing `System.Threading.Tasks.Task<T>`, also injects the `.await()`
  extension; bir2cir lowers the call site. Unifies on the one facadegen-surfaced Task (removes the "two Tasks").
- Result: the "kotc kotlin.clr coroutine injection seam" is never built. Impl check: confirm K2 accepts the
  two-actuals-across-two-builds arrangement (it is exactly the JvmName staging in build-stdlib-jar.sh).

### P2 → P3 handoff bugs (verified, must fix in P3)
1. **kotc `override val context` getter not marked override.** The cold-core `ContinuationImpl.get_context`
   (and `RestrictedContinuationImpl`) emit as `virtual:true` NewSlot rather than filling
   `BaseContinuationImpl`'s abstract `get_context` slot → a concrete SM subclass would TypeLoad-fail. P2 worked
   around it by re-overriding `get_context` in each synthesized SM; the ROOT cause is kotc not stamping the
   `override` getter as an override. Fix in kotc (P3), then drop the SM-side workaround.
2. **ilemit `coSuspendedSentinel` dead node** (`Emitter.Expressions.cs:72-73`) references a non-existent
   `IntrinsicsKt.COROUTINE_SUSPENDED` *field* — the real symbol is the property getter
   `get_COROUTINE_SUSPENDED()` (P2 references the getter directly, bypassing the node). Delete in P6.
