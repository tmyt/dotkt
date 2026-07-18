// Step-1 CLR stub mirroring the JVM `actual` declarations for this source set.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.coroutines

import kotlin.concurrent.Volatile
import kotlin.concurrent.atomics.interlockedCompareExchangeRef
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
import kotlin.coroutines.intrinsics.CoroutineSingletons.RESUMED
import kotlin.coroutines.intrinsics.CoroutineSingletons.UNDECIDED

// CLR: cache the boxed enum singletons ONCE — mirror COROUTINE_SUSPENDED_BOX (Intrinsics.kt:61). A CLR
// enum is a VALUE type; boxing it into an `Any?` slot on every read yields a DISTINCT reference, so the
// state-machine's `cur === UNDECIDED` / `current === RESUMED` reference-identity checks would always be
// false (a sync resume then falls to `else -> throw IllegalStateException("Already resumed")`). Storing
// the box once gives the stable identity the `===` checks depend on. (COROUTINE_SUSPENDED is already
// stabilized by its own cached box in the getter.) Read/write UNDECIDED/RESUMED ONLY via these.
private val UNDECIDED_BOX: Any = UNDECIDED
private val RESUMED_BOX: Any = RESUMED

// Mirrors the JVM SafeContinuation state machine faithfully, INCLUDING its concurrency. suspendCoroutine permits
// asynchronous resume from another thread, so getOrThrow (the suspending frame) and resumeWith (a dispatcher thread)
// can race the UNDECIDED transition; a plain check-then-store would let both win and clobber the marker or the value.
// The JVM guards this with an AtomicReferenceFieldUpdater CAS loop over a volatile field; the CLR analog is a
// `@Volatile` field (acquire/release + no JIT hoisting of the loop's re-read) plus Interlocked.CompareExchange on the
// field address (interlockedCompareExchangeRef, byref to `result`) — a genuine lock-free CAS, not plain field writes.
// CoroutineStackFrame is JVM-only and intentionally not implemented.
@PublishedApi
@SinceKotlin("1.3")
internal actual class SafeContinuation<in T>
internal actual constructor(
    private val delegate: Continuation<T>,
    initialResult: Any?
) : Continuation<T> {
    @PublishedApi
    internal actual constructor(delegate: Continuation<T>) : this(delegate, UNDECIDED_BOX)

    // @Volatile: the CAS loop below must re-read `result` each iteration (a plain field read could be hoisted by the
    // JIT into an infinite loop) and the state publication needs release/acquire visibility across dispatcher threads.
    @Volatile private var result: Any? = initialResult

    public actual override val context: CoroutineContext
        get() = delegate.context

    public actual override fun resumeWith(result: Result<T>) {
        while (true) {
            val cur = this.result
            when {
                cur === UNDECIDED_BOX -> {
                    // Publish the resume value; if we won the UNDECIDED->value CAS the frame will pick it up in getOrThrow.
                    if (interlockedCompareExchangeRef(this.result, result.value, UNDECIDED_BOX) === UNDECIDED_BOX) return
                }
                cur === COROUTINE_SUSPENDED -> {
                    // The frame already suspended; claim the SUSPENDED->RESUMED transition, then hand off to the delegate.
                    if (interlockedCompareExchangeRef(this.result, RESUMED_BOX, COROUTINE_SUSPENDED) === COROUTINE_SUSPENDED) {
                        delegate.resumeWith(result)
                        return
                    }
                }
                else -> throw IllegalStateException("Already resumed")
            }
        }
    }

    @PublishedApi
    internal actual fun getOrThrow(): Any? {
        var current = this.result
        if (current === UNDECIDED_BOX) {
            // Try to mark suspension; the CAS returns the value ACTUALLY present, so on a lost race we read the resume
            // value resumeWith just stored (no separate re-read needed).
            val prev = interlockedCompareExchangeRef(this.result, COROUTINE_SUSPENDED, UNDECIDED_BOX)
            if (prev === UNDECIDED_BOX) return COROUTINE_SUSPENDED // we suspended first; resume will come later
            current = prev
        }
        return when {
            current === RESUMED_BOX -> COROUTINE_SUSPENDED // already resumed delegate, indicate suspension upstream
            current is Result.Failure -> throw current.exception
            else -> current // either COROUTINE_SUSPENDED or the resumed data
        }
    }
}
