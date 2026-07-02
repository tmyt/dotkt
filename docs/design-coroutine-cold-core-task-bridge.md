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
