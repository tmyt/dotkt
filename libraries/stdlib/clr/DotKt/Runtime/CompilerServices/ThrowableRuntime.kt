package DotKt.Runtime.CompilerServices

@PublishedApi
internal interface ThrowableSlots {
    fun dotktCause(): Throwable?
}

@kotlin.clr.ClrTypeAlias("System.Exception")
private class NativeThrowable {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "InnerException")
    val innerException: Throwable? get() = null
}

// Native CLR exceptions do not implement a Kotlin slot. Preserve their object identity and native cause.
@PublishedApi
internal fun nativeThrowableCause(value: Any): Throwable? = (value as NativeThrowable).innerException

@PublishedApi
internal fun throwableCause(value: Any): Throwable? =
    if (value is ThrowableSlots) value.dotktCause() else nativeThrowableCause(value)
