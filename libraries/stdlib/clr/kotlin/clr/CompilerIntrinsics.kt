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
 * A compile-time handle for a CLR event.
 *
 * Event reads, subscriptions, and raises are resolved by the compiler against the concrete event declaration;
 * no `ClrEvent` value or class is emitted.
 */
public abstract class ClrEvent<out T> private constructor() {
    public abstract fun subscribe(handler: @UnsafeVariance T): EventSubscription<@UnsafeVariance T>

    public abstract operator fun invoke(vararg args: Any?)

    public abstract operator fun getValue(thisRef: Any?, property: KProperty<*>): ClrEvent<T>
}

/** Declares a Kotlin-owned CLR event for a delegated property. */
public fun clrEvent(): ClrEvent<Nothing> = TODO("compiler intrinsic")
