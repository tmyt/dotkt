// clr/taskinterop/: jar-EXCLUDED, CLR-build-ONLY (design note §5 — the frontend jar stays the pure
// Kotlin surface; build-stdlib-jar.sh skips this dir, build-stdlib-{ref,rt}.sh compile it).
//
// Frontend resolution split (design note §12 "P4 symbol-surfacing mechanism", user 2026-07-03):
//   - blockOn / delay: CLR-free SIGNATURES -> declared `expect` in the jar-INCLUDED common set
//     (libraries/stdlib/common/src/kotlin/clr/CoroutinesH.kt); the REAL `actual` bodies live below and
//     ride only ref.dll/rt.dll. The jar stages a throwing STUB actual (build-stdlib-jar.sh). So
//     `import kotlin.clr.blockOn`/`delay` resolve from the classpath with ZERO kotc special-casing.
//   - await: its signature names Task -> surfaced by facadegen (NOT expect/actual), bir2cir-lowered.
//
// The kotlin.clr coroutine surface (bundle-6 P1; names LOCKED by
// docs/design-coroutine-cold-core-task-bridge.md §11):
//   suspend fun <T> Task<T>.await(): T   — bir2cir-LOWERED at the call site (P4): awaiter fast path /
//   suspend fun Task0.await()              OnCompleted resume. The bodies here are placeholders that are
//                                          replaced by the lowering, never meant to run.
//   actual fun <T> blockOn(block): T     — the runBlocking analog: start `block` on the COLD CORE and
//                                          block the calling thread until the root completion resumes.
//   actual suspend fun delay(ms)         — Task.Delay(ms).await().
//
// blockOn is REAL Kotlin already (P1): it drains through a Monitor Wait/Pulse sink (the JVM
// RunSuspend.kt pattern) rather than `TaskCompletionSource.Task.<drain>` — same "start on the cold
// core + drain" contract, but it preserves RAW Kotlin exceptions (a Task drain via `.Result`/`Wait()`
// wraps faults in AggregateException, and unwrapping would need a TaskAwaiter STRUCT alias, unverified
// machinery). It throws NotImplementedError until suspend lambdas become real state machines (P3),
// because startCoroutine -> createCoroutineUnintercepted requires an SM value.
@file:Suppress("UNCHECKED_CAST")

package kotlin.clr

import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

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

/**
 * Runs [block] as a coroutine on the cold core and BLOCKS the calling thread until it completes —
 * the `runBlocking` analog for CLR entry points / tests. Rethrows the block's exception raw.
 */
public actual fun <T> blockOn(block: suspend () -> T): T {
    val sink = BlockOnSink()
    block.startCoroutine(sink) // Continuation<Any?> is a Continuation<T> by contravariance
    monitorEnter(sink)
    try {
        while (!sink.done) monitorWait(sink)
    } finally {
        monitorExit(sink)
    }
    sink.exception?.let { throw it }
    return sink.value as T
}

/** Suspends for [ms] milliseconds via `Task.Delay` (a value beyond Int.MAX_VALUE delays forever). */
public actual suspend fun delay(ms: Long) {
    if (ms <= 0) return
    // -1 is Timeout.Infinite; Task.Delay has no Int64 overload.
    clrTaskDelay(if (ms > Int.MAX_VALUE.toLong()) -1 else ms.toInt()).await()
}

// --- BCL primitives -----------------------------------------------------------------------------

@ClrIntrinsic("System.Threading.Tasks.Task.Delay")
private fun clrTaskDelay(millisecondsDelay: Int): Task0 = TODO("clr binding should be implemented")

// Monitor Wait/Pulse for the blockOn sink (Enter/Exit mirror the proven builtins/Atomics.kt bindings).
@ClrIntrinsic("System.Threading.Monitor.Enter")
private fun monitorEnter(lock: Any): Unit = TODO("clr binding should be implemented")
@ClrIntrinsic("System.Threading.Monitor.Exit")
private fun monitorExit(lock: Any): Unit = TODO("clr binding should be implemented")
@ClrIntrinsic("System.Threading.Monitor.Wait")
private fun monitorWait(lock: Any): Boolean = TODO("clr binding should be implemented")
@ClrIntrinsic("System.Threading.Monitor.Pulse")
private fun monitorPulse(lock: Any): Unit = TODO("clr binding should be implemented")

/** The blocking root sink: stores the outcome and pulses the [blockOn] waiter (all under this monitor). */
private class BlockOnSink : Continuation<Any?> {
    var done: Boolean = false
    var value: Any? = null
    var exception: Throwable? = null

    override val context: CoroutineContext
        get() = EmptyCoroutineContext

    override fun resumeWith(result: Result<Any?>) {
        monitorEnter(this)
        try {
            value = result.getOrNull()
            exception = result.exceptionOrNull()
            done = true
            monitorPulse(this)
        } finally {
            monitorExit(this)
        }
    }
}
