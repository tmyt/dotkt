@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")
// CLR `lazy` actuals. The default `lazy { }` is thread-safe (matching Kotlin/JVM and .NET's
// System.Lazy default): SYNCHRONIZED/PUBLICATION/no-mode -> SynchronizedLazyImpl (Monitor lock),
// only LazyThreadSafetyMode.NONE -> UnsafeLazyImpl.

package kotlin

import kotlin.concurrent.atomics.monitorEnter
import kotlin.concurrent.atomics.monitorExit

/**
 * Thread-safe [Lazy] implementation for the CLR.
 *
 * Memoization is guarded by a [System.Threading.Monitor] lock via [monitorEnter]/[monitorExit]
 * (bound as `@ClrIntrinsic` in `kotlin.concurrent.atomics`). We take the lock on EVERY `value`
 * read — i.e. NO lock-free double-checked-locking fast path — because on CLR the classic DCL fast
 * path needs a `volatile` read of the field to be memory-safe on weak-memory architectures (ARM),
 * and `@kotlin.concurrent.Volatile` is currently a no-op annotation on this target (no CLR binding
 * to a volatile field access). Correctness over speed: always-lock is unconditionally correct and
 * avoids a subtle publication bug the single-threaded gate cannot catch. The critical section can
 * run the user [initializer], which may throw, so the lock is released in a `finally`.
 */
internal class SynchronizedLazyImpl<out T>(initializer: () -> T, lock: Any? = null) : Lazy<T> {
    private var initializer: (() -> T)? = initializer
    private var _value: Any? = UNINITIALIZED_VALUE
    // final: reference-atomic, safe to read without the lock (used only as the monitor object).
    private val lock: Any = lock ?: this

    override val value: T
        get() {
            monitorEnter(lock)
            try {
                if (_value === UNINITIALIZED_VALUE) {
                    // `initializer!!()` fully constructs the value BEFORE the field is published;
                    // a reference-typed field write is atomic on .NET, so the sentinel/complete-value
                    // invariant holds and no torn value is ever observed.
                    _value = initializer!!()
                    initializer = null
                }
                @Suppress("UNCHECKED_CAST")
                return _value as T
            } finally {
                monitorExit(lock)
            }
        }

    override fun isInitialized(): Boolean = _value !== UNINITIALIZED_VALUE

    override fun toString(): String = if (isInitialized()) value.toString() else "Lazy value not initialized yet."
}

// Default `lazy { }` is thread-safe (Monitor lock), matching Kotlin/JVM and System.Lazy defaults.
public actual fun <T> lazy(initializer: () -> T): Lazy<T> = SynchronizedLazyImpl(initializer)

public actual fun <T> lazy(mode: LazyThreadSafetyMode, initializer: () -> T): Lazy<T> =
    when (mode) {
        // NONE is the only unsynchronized mode. SYNCHRONIZED and PUBLICATION both use the Monitor
        // lock: SYNCHRONIZED requires single-initializer mutual exclusion (exact match), and
        // PUBLICATION (which permits several initializer runs but a single published value) is
        // served correctly — if more strictly than required — by the same locked single-init impl.
        LazyThreadSafetyMode.SYNCHRONIZED -> SynchronizedLazyImpl(initializer)
        LazyThreadSafetyMode.PUBLICATION -> SynchronizedLazyImpl(initializer)
        LazyThreadSafetyMode.NONE -> UnsafeLazyImpl(initializer)
    }

public actual fun <T> lazy(lock: Any?, initializer: () -> T): Lazy<T> = SynchronizedLazyImpl(initializer, lock)
