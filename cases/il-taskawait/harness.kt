// Coroutine TEST HARNESS — NOT stdlib. `blockOn` (the runBlocking analog) was DROPPED from
// kotlin.clr (docs/design-coroutine-cold-core-task-bridge.md §13): it is a kotlinx/Track-2 primitive,
// re-implemented HERE in pure Kotlin over the PUBLIC stdlib primitives (startCoroutine/Continuation for
// the cold-core drive) + System.Threading.Monitor (the cross-thread Wait/Pulse drain). ZERO compiler
// special-casing — this harness IS a mini-Track-2, the living proof that runBlocking is ordinary library
// code over the shared cold core. The coroutine samples import `dotkt.support.blockOn` from here instead
// of a stdlib symbol.
@file:Suppress("UNCHECKED_CAST")

package dotkt.support

import System.Threading.Monitor
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

/**
 * Runs [block] as a coroutine on the cold core and BLOCKS the calling thread until it completes —
 * the `runBlocking` analog for CLR entry points / tests. Rethrows the block's exception raw. Drives a
 * BlockOnSink through a Monitor Wait/Pulse drain (the JVM RunSuspend.kt pattern): the caller waits under
 * the monitor while a synchronous OR threadpool-thread resumeWith sets the outcome and pulses.
 */
public fun <T> blockOn(block: suspend () -> T): T {
    val sink = BlockOnSink()
    block.startCoroutine(sink) // Continuation<Any?> is a Continuation<T> by contravariance
    Monitor.Enter(sink)
    try {
        while (!sink.done) Monitor.Wait(sink)
    } finally {
        Monitor.Exit(sink)
    }
    sink.exception?.let { throw it }
    return sink.value as T
}

/** The blocking root sink: stores the outcome and pulses the [blockOn] waiter (all under this monitor). */
private class BlockOnSink : Continuation<Any?> {
    var done: Boolean = false
    var value: Any? = null
    var exception: Throwable? = null

    override val context: CoroutineContext
        get() = EmptyCoroutineContext

    override fun resumeWith(result: Result<Any?>) {
        Monitor.Enter(this)
        try {
            value = result.getOrNull()
            exception = result.exceptionOrNull()
            done = true
            Monitor.Pulse(this)
        } finally {
            Monitor.Exit(this)
        }
    }
}
