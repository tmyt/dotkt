package kotlin.clr

import kotlin.reflect.KProperty

/**
 * A live managed reference used for CLR `ref`/`out` interop, user-defined non-suspend function parameters, and
 * delegated access to ref-returning storage. A parameter's referenced value is read or written through [value].
 *
 * This is a compile-time type. kotc projects it to BIR's managed-reference type; no `ClrRef` class is emitted.
 */
public class ClrRef<T> private constructor() {
    /** Reads or writes the value in the referenced CLR storage slot. */
    public var value: T
        get() = TODO("compiler intrinsic")
        set(value) { TODO("compiler intrinsic") }

    public operator fun getValue(thisRef: Any?, property: KProperty<*>): T =
        TODO("compiler intrinsic")

    public operator fun setValue(thisRef: Any?, property: KProperty<*>, value: T): Unit =
        TODO("compiler intrinsic")
}

/**
 * An opaque unmanaged CLR pointer projected from a `T*` metadata signature.
 *
 * This is a compile-time type. bir2cir replaces it with the exact unmanaged-pointer
 * signature; no `ClrPointer` class is emitted.
 */
public class ClrPointer<T> private constructor()

/**
 * An opaque CLR function pointer. [C] carries its calling convention and [F]
 * its callable signature, including signature-only types when required.
 * Unlike a Kotlin function or delegate, this does not retain a managed target.
 * This is a compile-time type; no wrapper class is emitted.
 */
public class ClrFunctionPointer<C, F> private constructor()

/** Marks [x] as an addressable CLR `ref`/`out` argument or a live ref-returning delegate. */
public fun <T> byref(x: T): ClrRef<T> = TODO("compiler intrinsic")

/**
 * A scoped stack-allocated buffer supplied by [stackBuffer].
 *
 * This is a compile-time type and cannot be constructed or stored independently.
 */
public class StackBuffer<T> private constructor() {
    public val size: Int
        get() = TODO("compiler intrinsic")

    public operator fun get(index: Int): T = TODO("compiler intrinsic")

    public operator fun set(index: Int, value: T): Unit = TODO("compiler intrinsic")

    public fun asSpan(): Span<T> = TODO("compiler intrinsic")
}

/** The Kotlin view of a CLR `System.Span<T>` produced by [StackBuffer.asSpan]. */
public class Span<T> private constructor()

/** Allocates [n] elements on the current stack frame and exposes them only for the duration of [block]. */
public fun <T, R> stackBuffer(n: Int, block: (StackBuffer<T>) -> R): R =
    TODO("compiler intrinsic")

/**
 * A retained handle for one CLR event and its source.
 *
 * [T] is the complete handler type, not an event payload. Handles may be stored, returned, captured, and passed
 * to ordinary functions. Reading an event evaluates its source once; each subscription uses that same source.
 * Raising remains a declaration operation on a Kotlin event implemented with [clrEvent], not on a retained handle.
 */
public class ClrEvent<out T> @PublishedApi internal constructor(
    private val add: (@UnsafeVariance T) -> Unit,
    private val remove: (@UnsafeVariance T) -> Unit,
) {
    public fun subscribe(handler: @UnsafeVariance T): EventSubscription<@UnsafeVariance T> {
        add(handler)
        return EventSubscription(handler, remove)
    }

    public operator fun invoke(vararg args: Any?): Unit = TODO("compiler intrinsic")

    public operator fun getValue(thisRef: Any?, property: KProperty<*>): ClrEvent<T> = this
}

/** Declares a Kotlin-owned CLR event for a delegated property. */
public fun clrEvent(): ClrEvent<Nothing> = TODO("compiler intrinsic")
