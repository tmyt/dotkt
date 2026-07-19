// Coroutine TEST HARNESS — NOT stdlib. A verbatim copy of cases/*/harness.kt (dotkt.support.blockOn):
// `blockOn` (the runBlocking analog) is a kotlinx/Track-2 primitive re-implemented in pure Kotlin over the
// PUBLIC stdlib cold-core primitives (startCoroutine/Continuation) + System.Threading.Monitor. ZERO compiler
// special-casing. The coroutine fixtures import `dotkt.support.blockOn` from here. In the full harness this
// single shared file replaces the 36 duplicated harness.kt copies the audit (§13) condemns.
@file:Suppress("UNCHECKED_CAST")

package dotkt.support

import System.Threading.Monitor
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

/**
 * Runs [block] as a coroutine on the cold core and BLOCKS the calling thread until it completes —
 * the `runBlocking` analog for CLR entry points / tests. Rethrows the block's exception raw.
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
