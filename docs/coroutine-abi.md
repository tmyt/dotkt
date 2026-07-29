# Kotlin `suspend` to CLR ABI

This document fixes the public boundary between Kotlin coroutines and ordinary CLR callers. The internal lowering is described in [design-coroutine-cold-core-task-bridge.md](design-coroutine-cold-core-task-bridge.md).

## Public contract

| Kotlin declaration | CLR-visible method |
|---|---|
| `suspend fun f(...): T` | `Task<T> F(...)` |
| `suspend fun f(...): Unit` | `Task F(...)` |

The public method has no hidden `Continuation` parameter. C# and F# consume it as an ordinary task-returning method:

```csharp
var value = await Namespace.F(...);
```

A call starts the computation immediately. Synchronous completion returns an already-completed task; failure is represented by a faulted task.

## Internal contract

Kotlin-to-Kotlin suspend calls use a cold continuation-based entry point conceptually shaped as:

```text
f$dotkt_suspend(args..., completion: Continuation<T>): Any?
```

It returns the result directly when it completes synchronously or `COROUTINE_SUSPENDED` when it will resume the supplied continuation later. Kotlin calls do not route through the public `Task<T>` wrapper.

At a CLR export boundary, DotKt creates a root continuation backed by `TaskCompletionSource<T>` and connects the cold entry point to the public task. This preserves Kotlin coroutine composition internally while presenting a conventional .NET API.

## Awaiting .NET tasks from Kotlin

The reverse bridge is supplied as metadata-only suspend members in the corresponding reference KLIBs:

```kotlin
suspend fun Task.await(): Unit
suspend fun <T> Task<T>.await(): T
```

The fast path uses `GetAwaiter().GetResult()` when the task is complete. Otherwise the current coroutine suspends, registers a completion callback, and resumes through the task awaiter.

## Exceptions and cancellation

- An exception escaping an exported suspend function faults its returned task.
- An exception from an awaited task is rethrown into the Kotlin coroutine through the awaiter.
- Kotlin `CancellationException` is mapped to CLR cancellation semantics at the task boundary where supported.
- `CancellationToken` is not an implicit parameter of every suspend function. Token-based APIs are explicit interop and library design.

## Non-contractual details

State-machine class names, generated helper names, `TaskCompletionSource` allocation strategy, and synchronous fast-path optimizations are implementation details. They may change without changing the ABI above.

Known bugs and unsupported shapes belong in [GitHub Issues](https://github.com/tmyt/dotkt/issues), not in this contract.
