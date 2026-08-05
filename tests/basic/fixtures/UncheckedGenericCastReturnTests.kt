import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.Type

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

private fun interface NestedUncheckedCastSam<T> {
    fun read(): T
}

@Suppress("UNCHECKED_CAST")
fun <T> throughNestedClosure(value: T, raw: Any?, invokeNested: Boolean): T {
    val nested = { raw as T }
    if (invokeNested) nested()
    return value
}

@Suppress("UNCHECKED_CAST")
fun <T> throughNestedSam(value: T, raw: Any?, invokeNested: Boolean): T {
    val nested = NestedUncheckedCastSam { raw as T }
    if (invokeNested) nested.read()
    return value
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

    @TestAttribute
    fun nestedClosureReturnDoesNotEraseEnclosingMethodReturn() {
        assertEquals(41, throughNestedClosure(41, null, false))
        assertEquals(42, throughNestedClosure(42, 7, true))

        val method = Type.GetType("UncheckedGenericCastReturnTestsKt")!!
            .GetMethod("throughNestedClosure")!!
        assertTrue(method.ReturnType.IsGenericParameter)
    }

    @TestAttribute
    fun nestedSamReturnDoesNotEraseEnclosingMethodReturn() {
        assertEquals(41, throughNestedSam(41, null, false))
        assertEquals(42, throughNestedSam(42, 7, true))

        val method = Type.GetType("UncheckedGenericCastReturnTestsKt")!!
            .GetMethod("throughNestedSam")!!
        assertTrue(method.ReturnType.IsGenericParameter)
    }
}
