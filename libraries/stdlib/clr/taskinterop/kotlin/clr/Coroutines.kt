// clr/taskinterop/: the CLR platform Task bridge (design note §5). Lives under libraries/stdlib/clr/,
// so all three stdlib builds compile it (collect_stdlib_sources in lib.sh feeds klib/ref/rt alike).
//
// The core kotlin.clr coroutine surface is JUST `await` (docs/design-coroutine-cold-core-task-bridge.md
// §13, user 2026-07-03). `blockOn`/`delay` were DROPPED from the stdlib — they are kotlinx/Track-2
// primitives, not stdlib, and are re-implemented in the TEST HARNESS in pure Kotlin over the public
// primitives (startCoroutine/Continuation for blockOn's drain; Task.Delay(ms).await() for delay). The
// compiler never emits either — every compiler-emitted coroutine driver uses the low-level machinery
// (SM / TCS / RootContinuation / drain), never blockOn.
//
//   suspend fun <T> Task<T>.await(): T   — bir2cir-LOWERED at the call site (P4): awaiter fast path /
//   suspend fun Task0.await()              OnCompleted resume. `await`'s signature names Task, so it is
//                                          surfaced by facadegen (NOT expect/actual). The bodies here are
//                                          placeholders replaced by the lowering, never meant to run.
@file:Suppress("UNCHECKED_CAST")

package kotlin.clr

/**
 * Awaits completion of this `Task<T>` without blocking the thread: suspends until the task completes,
 * returns its result, or rethrows its exception with normal .NET `await` semantics
 * (`GetAwaiter().GetResult()` — no AggregateException wrapping).
 */
public suspend fun <T> Task<T>.await(): T =
    TODO("bir2cir-lowered (bundle-6 P4): the Task.await call site becomes the TaskAwaiter + Continuation bridge")

/** The non-generic `Task` form of [await]. */
public suspend fun Task0.await(): Unit =
    TODO("bir2cir-lowered (bundle-6 P4): the Task.await call site becomes the TaskAwaiter + Continuation bridge")
