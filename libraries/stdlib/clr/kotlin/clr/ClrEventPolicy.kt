package kotlin.clr

import kotlin.reflect.KProperty

/**
 * Storage and dispatch policy for a CLR event declared with `by clrEvent(policy)`.
 *
 * [T] is the complete handler, not an event payload. The compiler provides the
 * native event accessors; the policy owns registration, removal and dispatch.
 * A retained [ClrEvent] remains the consumer's subscription handle.
 */
public abstract class ClrEventPolicy<out T> protected constructor() {
    protected abstract fun add(handler: @UnsafeVariance T)
    protected abstract fun remove(handler: @UnsafeVariance T)

    /** Dispatches the declared event's arguments according to this policy. */
    public abstract operator fun invoke(vararg args: Any?)

    @PublishedApi
    internal fun addHandler(handler: @UnsafeVariance T) { add(handler) }

    @PublishedApi
    internal fun removeHandler(handler: @UnsafeVariance T) { remove(handler) }

    /** Ordinary delegation without [clrEvent] produces a handle-valued property. */
    public operator fun getValue(thisRef: Any?, property: KProperty<*>): ClrEvent<T> =
        ClrEvent(::add, ::remove)
}

/** Supplies [policy] for a native event declared with `by clrEvent(policy)`. */
public fun <T> clrEvent(policy: ClrEventPolicy<T>): ClrEventPolicy<T> = policy
