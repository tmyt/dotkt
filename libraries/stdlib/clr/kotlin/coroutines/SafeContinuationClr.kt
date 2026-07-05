// Step-1 CLR stub mirroring the JVM `actual` declarations for this source set.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.coroutines

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

// Mirrors the JVM SafeContinuation state machine. The CLR coroutine ABI is
// Task-based and a continuation is resumed exactly once, so the JVM-only
// AtomicReferenceFieldUpdater lock-free loop is replaced with plain field
// reads/writes. CoroutineStackFrame is JVM-only and intentionally not implemented.
@PublishedApi
@SinceKotlin("1.3")
internal actual class SafeContinuation<in T>
internal actual constructor(
    private val delegate: Continuation<T>,
    initialResult: Any?
) : Continuation<T> {
    @PublishedApi
    internal actual constructor(delegate: Continuation<T>) : this(delegate, UNDECIDED_BOX)

    private var result: Any? = initialResult

    public actual override val context: CoroutineContext
        get() = delegate.context

    public actual override fun resumeWith(result: Result<T>) {
        val cur = this.result
        when {
            cur === UNDECIDED_BOX -> {
                this.result = result.value
            }
            cur === COROUTINE_SUSPENDED -> {
                this.result = RESUMED_BOX
                delegate.resumeWith(result)
            }
            else -> throw IllegalStateException("Already resumed")
        }
    }

    @PublishedApi
    internal actual fun getOrThrow(): Any? {
        if (result === UNDECIDED_BOX) {
            result = COROUTINE_SUSPENDED
            return COROUTINE_SUSPENDED
        }
        val current = result
        return when {
            current === RESUMED_BOX -> COROUTINE_SUSPENDED // already resumed delegate, indicate suspension upstream
            current is Result.Failure -> throw current.exception
            else -> current // either COROUTINE_SUSPENDED or the resumed data
        }
    }
}
