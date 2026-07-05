@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")
// CLR `lazy` actuals. The default `lazy { }` is thread-safe (matching Kotlin/JVM and .NET's
// System.Lazy default): SYNCHRONIZED/PUBLICATION/no-mode -> SynchronizedLazyImpl (Monitor lock),
// only LazyThreadSafetyMode.NONE -> UnsafeLazyImpl.

package kotlin

import kotlin.concurrent.Volatile
import kotlin.concurrent.atomics.monitorEnter
import kotlin.concurrent.atomics.monitorExit

/**
 * Thread-safe [Lazy] implementation for the CLR.
 *
 * Memoization is guarded by a [System.Threading.Monitor] lock via [monitorEnter]/[monitorExit]
 * (bound as `@ClrIntrinsic` in `kotlin.concurrent.atomics`), using the classic **double-checked
 * locking (DCL)** shape — exactly like Kotlin/JVM's `SynchronizedLazyImpl`:
 *
 *  - The fast path is a **lock-free `@Volatile` read** of [_value]. Once the value is published, no
 *    lock is ever taken again, so a fully-initialized `lazy` costs a single volatile field load.
 *  - The slow path takes the Monitor lock and re-checks [_value] (the *second* check) before running
 *    the user [initializer] exactly once.
 *
 * The DCL fast path is memory-safe **because [_value] is `@Volatile`**: on the CLR that lowers to a
 * genuine volatile field (`modreq(IsVolatile)` + `volatile.` prefix — the exact C# `volatile`
 * encoding, see docs/dotkt-semantics.md §4c), giving the fast-path read acquire semantics and the
 * publishing write release semantics on weak-memory architectures (ARM). This was previously
 * impossible while `@Volatile` was a no-op, which is why this impl used to always-lock. The critical
 * section can run the user [initializer], which may throw, so the lock is released in a `finally`.
 */
internal class SynchronizedLazyImpl<out T>(initializer: () -> T, lock: Any? = null) : Lazy<T> {
    private var initializer: (() -> T)? = initializer
    // @Volatile: the DCL lock-free fast-path read of this field is memory-safe only because the field
    // is a real CLR volatile field (acquire on load / release on the publishing store). A reference-
    // typed write is atomic on .NET, so the sentinel/complete-value invariant holds — no torn value.
    @Volatile private var _value: Any? = UNINITIALIZED_VALUE
    // final: reference-atomic, safe to read without the lock (used only as the monitor object).
    private val lock: Any = lock ?: this

    override val value: T
        get() {
            // Fast path: a lock-free volatile read. Once published, `value` never takes the lock again.
            val v1 = _value
            if (v1 !== UNINITIALIZED_VALUE) {
                @Suppress("UNCHECKED_CAST")
                return v1 as T
            }
            // Slow path: take the lock and re-check (the second "check" of double-checked locking).
            monitorEnter(lock)
            try {
                val v2 = _value
                if (v2 !== UNINITIALIZED_VALUE) {
                    @Suppress("UNCHECKED_CAST")
                    return v2 as T
                }
                // `initializer!!()` fully constructs the value BEFORE the volatile store publishes it.
                val typed = initializer!!()
                _value = typed
                initializer = null
                return typed
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
