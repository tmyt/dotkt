package roundtrip.protectedgenericowner

open class ReferencedProtectedGenericOwnerBase<T> {
    protected fun snapshot(values: Array<T?>): Array<T?> = values
    protected open fun openSnapshot(values: Array<T?>): Array<T?> = values
}
