import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

private class DeferredGenericCast<T> {
    @Suppress("UNCHECKED_CAST")
    private fun read(raw: Any?): T = raw as T

    private fun consume(value: T): Int = value.hashCode()

    fun consumeOnlyWhenRequested(consume: Boolean, raw: Any?): Int {
        val value = read(raw)
        if (!consume) return 7
        return this.consume(value)
    }
}

private class GenericPayload<T>(val value: T)

private open class VirtualGenericCast<T> {
    @Suppress("UNCHECKED_CAST")
    protected open fun read(raw: Any?): T = raw as T

    fun dispatch(raw: Any?): T = read(raw)
}

private class VirtualGenericUnwrapper<T> : VirtualGenericCast<T>() {
    @Suppress("UNCHECKED_CAST")
    override fun read(raw: Any?): T = (raw as GenericPayload<T>).value
}

class UncheckedGenericCastReturnTests {
    @TestAttribute
    fun nullableCarrierIsNarrowedAtUseInsteadOfGenericMethodReturn() {
        val reader = DeferredGenericCast<Int>()
        assertEquals(7, reader.consumeOnlyWhenRequested(false, null))
        assertEquals(42, reader.consumeOnlyWhenRequested(true, 42))
    }

    @TestAttribute
    fun virtualGenericCastKeepsOneHierarchyWideClrSlot() {
        assertEquals(42, VirtualGenericUnwrapper<Int>().dispatch(GenericPayload(42)))
    }
}
