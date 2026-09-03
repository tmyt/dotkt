import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.Type
import kotlin.clr.ClrField as KotlinClrField

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

private open class ProtectedGenericCast<T> {
    @Suppress("UNCHECKED_CAST")
    protected fun read(raw: Any?): T = raw as T
}

private class ProtectedIntArithmetic : ProtectedGenericCast<Int>() {
    fun add(raw: Any?, delta: Int): Int = read(raw) + delta
    fun negate(raw: Any?): Int = -read(raw)
}

private open class ProtectedNullableProperty<T>(protected val stored: T?)

private class ProtectedNullableInt(initial: Int?) : ProtectedNullableProperty<Int>(initial) {
    fun add(delta: Int): Int = (stored ?: 0) + delta
}

private class InlinePrivateNullableField<T>(initial: T?) {
    @KotlinClrField private val stored: T? = initial

    inline fun <R> consume(block: (T?) -> R): R = block(stored)
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
    fun protectedUncheckedCastKeepsConcreteOperatorProjectionThroughUnsafeAccessor() {
        val arithmetic = ProtectedIntArithmetic()
        assertEquals(42, arithmetic.add(40, 2))
        assertEquals(-42, arithmetic.negate(42))
    }

    @TestAttribute
    fun protectedNullableGenericPropertyKeepsItsObjectErasureThroughUnsafeAccessor() {
        assertEquals(42, ProtectedNullableInt(40).add(2))
        assertEquals(2, ProtectedNullableInt(null).add(2))
    }

    @TestAttribute
    fun inlinePrivateNullableGenericFieldKeepsItsObjectErasureThroughUnsafeAccessor() {
        assertEquals(42, InlinePrivateNullableField<Int>(42).consume { it })
        assertEquals(null, InlinePrivateNullableField<Int>(null).consume { it })
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
