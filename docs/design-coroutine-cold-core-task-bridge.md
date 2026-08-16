# Coroutine cold core + CLR Task bridge design note

Status: implemented design record (2026-07-03). This supplies the internal implementation behind
the public [coroutine ABI](coroutine-abi.md).

The short version:

- Kotlin-facing coroutine bodies should be cold, Continuation-based computations.
- CLR-facing `suspend fun` exports should remain hot `Task<T>` / `Task` methods.
- `.NET Task -> Kotlin suspend` interop should be a CLR platform extension (`Task.await`) supplied outside the
  frontend stdlib klib.
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

The reverse bridge is a metadata-only suspend member synthesized into each conforming Task reference KLIB:

```kotlin
task.await()
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

`Task.await` should not be compiled into the frontend stdlib klib. The klib should remain the pure Kotlin stdlib surface
and should not contain `System.Threading.Tasks.Task`.

Instead:

```text
dll2klib
  Projects a metadata-only @ClrAwaitBridge suspend declaration directly onto each conforming
  awaitable in its reference KLIB.

kotc
  Resolves that ordinary KLIB declaration and preserves the await marker as a BIR fact.

bir2cir
  Resolves the awaitable pattern from compile-reference metadata and lowers the marked call to
  GetAwaiter / IsCompleted / OnCompleted / GetResult.
```

This keeps the split:

```text
stdlib klib:
  kotlin.* pure frontend symbols

reference KLIBs:
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

dll2klib:
  - expose .NET types to Kotlin metadata
  - project CLR platform extensions such as kotlin.clr.await(Task)
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

4. Project CLR platform `Task.await` metadata through dll2klib and lower it in bir2cir.

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
- `Task.await` is a CLR platform extension, not a pure stdlib klib API.
- CLR delegate interop performs explicit conversion between Kotlin suspend function values and Task-returning delegates.
- `Sequence` and `IAsyncEnumerable` are cold multi-shot adapters, not Task-shaped coroutines.
- Builder/sink selection should be explicit: known stdlib/kotlinx builders or marker metadata, not receiver-name
  guessing.

## 11. Implementation contract (P0 lock, 2026-07-03 — the approved bundle-6 plan)

Locked decisions the implementation phases (P1-P6) built against. Remaining defects are tracked in
GitHub Issues.

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

### Await and blocking surfaces

```kotlin
task.await() // metadata-only member synthesized into the task's reference KLIB
```

Blocking or scheduling helpers such as `runBlocking` and `delay` are library APIs layered over the coroutine core;
they are not compiler-provided `kotlin.clr` declarations.

### v1 limits (policy = call-time NotSupportedException, never an emit crash)

- No suspension inside `catch`/`finally` blocks (try/catch AROUND suspension works).
- No `suspendCancellableCoroutine` (kotlinx — purged). Plain `suspendCoroutine` works E2E, INCLUDING
  cross-module (F2, 2026-07-05): our compiler does not inline `@InlineOnly` cross-module, so an APP's
  `suspendCoroutine { … }` reaches bir2cir un-inlined (a plain `callStatic suspendCoroutine(<closure>)`,
  its wrapper body NOT inlined at the call site). `SuspendColdLowering` recognizes that shape
  (`IsSuspendCoroutineCall`) and RECONSTRUCTS the wrapper body inside the caller's cold SM — buffer a
  (possibly synchronous) resume through a `SafeContinuation`, run the block against it, take
  `getOrThrow()` as the suspension result. `SafeContinuation`'s ctor/`getOrThrow` are `internal`, so the
  reconstruction routes through the PUBLIC `clr.internal` bridges `newSafeContinuation`/`safeGetOrThrow`
  (which keep the internal type inside the stdlib). `SafeContinuation` caches its `UNDECIDED`/`RESUMED`
  boxed enums (F1) so a sync resume's `cur === UNDECIDED` identity check holds on the CLR (a boxed value
  type has unstable `===` identity otherwise). Same-module `suspendCoroutine*` still lowers via the
  inlined `valueBlock` intrinsic (`EmitIntrinsicSuspension`). Coverage:
  `tests/coroutines/fixtures/ContinuationBridgeTests.kt`.
- No CancellationToken/Job/interceptor dispatch (later layers); `intercepted()` = identity v1.

### Supersession notes

- Earlier `IAsyncStateMachine`/`AsyncTaskMethodBuilder` and TypedCont/Builders designs are superseded.
  They remain available in Git history. The implemented state machine is `ContinuationImpl`-based
  plain CIR, while the hot-Task public ABI remains unchanged.

## 12. Ownership

- The standard library owns the Kotlin continuation protocol and its reusable cold-core bases.
- `dll2klib` recognizes the CLR awaitable pattern and projects metadata-only `await` declarations into reference KLIBs.
- `kotc` resolves those declarations normally and preserves only Kotlin suspend semantics plus the await marker in BIR.
- `bir2cir` owns both physical directions at the CLR boundary: awaiter calls for `.await()` and the public
  `Task`/`Task<T>` bridge for exported suspend functions. It resolves the BCL Task family from compile-reference
  metadata and synthesizes a module-private root continuation backed by `TaskCompletionSource<T>`.
- `ilemit` emits the resulting CIR without reconstructing coroutine or Task semantics.

### P2 → P3 handoff bugs (verified, must fix in P3)
1. **kotc `override val context` getter not marked override.** The cold-core `ContinuationImpl.get_context`
   (and `RestrictedContinuationImpl`) emit as `virtual:true` NewSlot rather than filling
   `BaseContinuationImpl`'s abstract `get_context` slot → a concrete SM subclass would TypeLoad-fail. P2 worked
   around it by re-overriding `get_context` in each synthesized SM; the ROOT cause is kotc not stamping the
   `override` getter as an override. Fix in kotc (P3), then drop the SM-side workaround.
2. **ilemit `coSuspendedSentinel` dead node** (`Emitter.Expressions.cs:72-73`) references a non-existent
   `IntrinsicsKt.COROUTINE_SUSPENDED` *field* — the real symbol is the property getter
   `get_COROUTINE_SUSPENDED()` (P2 references the getter directly, bypassing the node). Delete in P6.

## 13. `blockOn` AND `delay` are DROPPED from kotlin.clr — re-implemented in the TEST HARNESS (user, 2026-07-03)

Neither is a stdlib primitive: in upstream Kotlin `delay`/`runBlocking` live in `kotlinx.coroutines`
(structured concurrency / dispatcher), NOT `kotlin-stdlib`. They were pushed into `kotlin.clr` only as a
test crutch after the kotlinx purge — a category error. The compiler NEVER needs to emit either: every
compiler-emitted coroutine driver — the synthesized `suspend fun main` drain, the public `Task<T>` bridge,
and Kotlin→Kotlin direct cold calls — uses the low-level machinery (SM / TCS / RootContinuation / sync-or-
`async Task Main` drain), not `blockOn`. The user-facing "run a coroutine from a sync .NET context" case is
served natively by the `Task<T>` bridge + `.GetAwaiter().GetResult()` (.NET's own blocking).

**Decision:**
- **`blockOn` and `delay` are REMOVED from `kotlin.clr`** (the `expect` in `common/src/kotlin/clr/CoroutinesH.kt`,
  the jar stub actual staged in `build-stdlib-jar.sh`, and the real actuals in `taskinterop/Coroutines.kt`).
- **The compiler-projected CLR coroutine surface is just `await`** — the genuine CLR async boundary.
  Proper `blockOn`/`delay`/`launch`/`async` (with cancellation + dispatcher)
  are a future **Track 2** (kotlinx port).
- **`blockOn`/`delay` are re-implemented IN THE TEST HARNESS, in pure Kotlin, over the PUBLIC stdlib primitives**
  (`startCoroutine`/`Continuation` for blockOn's drain; `Task.Delay(ms).await()` for delay). The coroutine test
  samples import the harness helpers instead of a stdlib symbol.

**Why this is the validation, not just a test convenience:** the cold-core thesis is "sequence/Flow/kotlinx
builders are ordinary library code over the shared cold core — the compiler needs NO builder knowledge." A
test harness that implements the kotlinx primitives `blockOn`/`delay` in pure Kotlin over our stdlib
primitives, with ZERO compiler special-casing, is a LIVING PROOF of exactly that claim. If the harness can't
express them, that surfaces a real primitive gap — the best possible test. The harness IS a mini-Track-2.

Harness location: shared Kotlin support under `tests/support/`, compiled with the coroutine tests; roundtrip
scenarios keep their own inline copy. Sequencing:
harness `blockOn` needs only `startCoroutine` (available after wave-2b lambda SMs); harness `delay` needs
`await` (P4) — so `delay`-using tests wait on P4, `blockOn`-using tests land right after wave-2b.

## 14. R1 — "Declaration is unconditional": the cold-entry ABI is an invariant, not a proof (#90/#100/#101)

The original `SuspendColdLowering` treated "a suspend callee has a cold entry" as a property to be PROVEN
by an allow-list FIXPOINT over shape-eligible bodies: a shape gate (`IsShapeEligible`/`IsMemberShapeEligible`)
first FILTERED the registry to segmentable funs, then a fixpoint DROPPED any fun whose suspend callee could
not be resolved to a same-assembly transformable entry or a ref.dll Suspend-flagged member. Every resolvability
miss (an interface/base member, a static member, a shape-refused body, a `clr*` referenced call) cascaded into
a DROP — and a dropped fun kept `suspend:true`, reaching ilemit un-lowered → a hard emit-time ICE (the #101
contradiction of this doc's §11 "never an emit crash"; the #90 "~10 fns broken IL"; the #100 unguarded clr*
rewrite). Kotlin/JVM parity is the opposite model: EVERY `suspend fun` (abstract/interface/concrete)
UNCONDITIONALLY compiles to the continuation-passing form.

**R1 replaces the eligibility FILTER with a CLASSIFIER.** Every suspend member declared in the compilation
gets a cold-entry slot `<name>$dotkt_suspend(params…, Continuation<Any?>): Any?` + a lockstep Task bridge
UNCONDITIONALLY. The classifier assigns each admitted member one of three shapes:

| Classification | Shape emitted |
|---|---|
| abstract member / interface-no-body | abstract cold entry + abstract Task bridge (no SM) |
| concrete + segmentable (`SuspensionRefusalReason == null`) | SM class + cold entry + bridge (the full transform) |
| concrete + NOT segmentable (v1 limit, or M4 own-generic-on-generic-class) | a call-time `throw NotSupportedException(reason)` cold entry + bridge, and a bir2cir WARNING naming the fun + the refusal site |

The only members NOT admitted are the reference-KLIB `@ClrAwaitBridge` declarations, the old kotc CPS/sequence
path (`steps`/`coClass`), and stdlib inline coroutine intrinsics (`suspendCoroutine*`, left un-lowered in stdlib
builds because their call sites are reconstructed inline). A declaration that is not admitted keeps no emittable
body: `SuspendResidueLowering` replaces it with an explicit `throw NotSupportedException(...)` in CIR — as it does
for the stdlib self-build's RETAINED original beside its cold entry — and the `suspend` modifier is dropped once
`[KotlinFunction(Suspend)]` has been stamped from it, so no Kotlin coroutine vocabulary reaches ilemit. An app build
has no such residue, and a survivor there is refused in bir2cir.

**Consequences (all deletions land in the same change):**

- **The resolvability fixpoint, `IsResolvable`, the `AllSupers` hierarchy walk, and the L3 drop warning are
  DELETED.** Same-assembly resolvability holds BY CONSTRUCTION — the callee's cold entry always exists
  (concrete or abstract, but always a callable slot). Call-site rewrite (`callStatic`/`callInstance`/`clr*`)
  is UNCONDITIONAL. An inherited or overridden suspend member is resolved by NATIVE virtual dispatch through
  the virtual/override-lockstep cold slot — no hierarchy analysis in bir2cir.
- **The v1-non-segmentable set** (suspension in a catch/finally, a nested suspending try, a suspension in a
  disallowed lambda position, M4) now COMPILES with a call-time throw rather than an emit ICE. Both call
  paths observe the throw: a Kotlin→Kotlin cold call propagates it synchronously; the public Task bridge
  catches it and faults the Task (`RootContinuation.resumeWith` → `TrySetException`, NSE is not OCE so
  faulted, not canceled). A C# caller that drops the returned faulted Task never observes the NSE — the
  standard .NET async contract, accepted as Kotlin-faithful.
- **Suspend lambdas use the same classifier (#125).** A non-segmentable `newSuspendLambda` still becomes a valid
  `SuspendLambda` state machine with the normal capture/create protocol, but its `invokeSuspend` body throws the same
  explanatory `NotSupportedException` at invocation. It never embeds an unsegmented `suspendCall` in emitted IL.
- **M3 (static/companion members) enter the classifier.** kotc promotes a `companion object` suspend fun to a
  `static` method on the outer class; its cold entry/bridge stay static (no `$this`), the SM is top-level-shaped,
  and the bridge's cold call targets the enclosing class owner (not `owner:null` = the file class).

**R1b — the cross-assembly `clr*` existence guard (#100).** A `clr*` suspend call is rewritten to the cold
entry on the REFERENCED owner. bir2cir reads the ref.dll (`DotKt.Private.Stdlib.dll`), which carries the
`[KotlinFunction(Suspend)]` flag (`MemberBinding.Suspend`) on the Kotlin surface member. The R1b existence
check consults that SUSPEND FLAG through the referenced owner's reflected hierarchy
(`HasSuspendMemberInHierarchy`) rather than probing for a literal `$dotkt_suspend` method: the flag is the
declared Kotlin fact, while the cold entry's presence in a particular referenced artifact is a property of how
that artifact was produced. Flag present ⇒ the cold ABI exists by R1's invariant ⇒ rewrite. Flag ABSENT ⇒ a hard,
actionable bir2cir error (the referenced assembly predates the cold ABI or is a hand-written .NET assembly) —
no dual-track fallback. The `await` marker and `suspendCoroutine*` intrinsics are intercepted upstream and
never reach the guard. NB the flag check proves "a suspend member of an assembly", not "an assembly built
post-cold-ABI"; a stale third-party DotKt dll (flag present, no cold entry) would pass the guard and fail
downstream — safe for the stdlib (ref/rt ship in lockstep), a 0.9.8 assembly-level ABI-version attribute if
third-party staleness ever matters.
