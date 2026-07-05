// bir2cir SuspendColdLowering F2 — a CROSS-MODULE `suspendCoroutine { … }`. Our compiler does NOT inline
// @InlineOnly cross-module, so the app carries a plain (un-inlined) `suspendCoroutine` call with the block
// materialized as a closure/lambda. bir2cir reconstructs the wrapper's SafeContinuation body inside the
// caller's cold state machine (routing the internal SafeContinuation through the public clr-internal bridges
// newSafeContinuation/safeGetOrThrow). F1 — SafeContinuation caches the UNDECIDED/RESUMED boxed enums, so a
// SYNC resume's `cur === UNDECIDED` identity check holds (else it wrongly throws "Already resumed"). Drained
// by the synthesized plain `main` (sync-completion path).
import kotlin.coroutines.suspendCoroutine
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

// A synchronous resume: the block resumes immediately with 42 (never actually suspends).
suspend fun sc(): Int = suspendCoroutine { it.resume(42) }

// A synchronous resumeWithException: getOrThrow() rethrows the failure at the (sync) suspension point.
suspend fun scThrow(): Int = suspendCoroutine { it.resumeWithException(IllegalStateException("boom")) }

suspend fun main() {
    println(sc())
    try {
        println(scThrow())
    } catch (e: IllegalStateException) {
        println("caught:" + e.message)
    }
}
