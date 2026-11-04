package roundtrip.protectedgenericowner

open class ReferencedProtectedGenericOwnerBase<T> {
    protected fun snapshot(values: Array<T?>): Array<T?> = values
    protected open fun openSnapshot(values: Array<T?>): Array<T?> = values
}

open class ReferencedProtectedErasedProperty<T>(private val raw: Any?) {
    @Suppress("UNCHECKED_CAST")
    protected val current: T get() = raw as T
}
