package roundtrip.protectedmethodgeneric

open class ReferencedProtectedMethodGenericBase {
    protected fun <T> snapshot(values: Array<T?>): Array<T?> = values
}
