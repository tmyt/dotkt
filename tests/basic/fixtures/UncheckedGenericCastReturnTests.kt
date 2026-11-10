import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
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

private open class ProtectedUncheckedProperty<T>(private val raw: Any?) {
    @Suppress("UNCHECKED_CAST")
    protected val current: T get() = raw as T
}

private class ProtectedPropertyInt : ProtectedUncheckedProperty<Int>(40) {
    val constructed: Int = current + 2
    fun add(delta: Int): Int = current + delta
    fun negate(): Int = -current
}

private class ProtectedPropertyText : ProtectedUncheckedProperty<String>("text") {
    fun length(): Int = current.length
}

private class ProtectedPropertyNullable(raw: Int?) : ProtectedUncheckedProperty<Int?>(raw) {
    fun add(delta: Int): Int = (current ?: 0) + delta
}

private class ProtectedPropertyDeferred<T>(raw: Any?) : ProtectedUncheckedProperty<T>(raw) {
    val initialized: Int
    init {
        val initial = current
        initialized = if (raw == null) 7 else initial.hashCode()
    }

    fun consume(used: Boolean): Int {
        val value = current
        if (!used) return 7
        return value.hashCode()
    }
}

private class ProtectedPropertyIgnored : ProtectedUncheckedProperty<Int>(null) {
    fun read(): Int {
        current
        return 7
    }
}

private class ProtectedPropertyIgnoredText : ProtectedUncheckedProperty<String>(42) {
    fun read(): Int {
        current
        return 7
    }
}

private open class ProtectedPropertySink(val result: Int)

private class ProtectedPropertyOuter : ProtectedUncheckedProperty<Int>(40) {
    inner class Built : ProtectedPropertySink(current + 2)
}

private class ProtectedPropertyCopy(raw: Any?) : ProtectedUncheckedProperty<Int>(raw) {
    constructor(other: ProtectedPropertyCopy, delta: Int) : this(other.current + delta)
    fun read(): Int = current
}

private class ProtectedPropertyStatic(raw: Any?) : ProtectedUncheckedProperty<Int>(raw) {
    companion object { val total: Int = ProtectedPropertyStatic(40).current + 2 }
}

private open class PublicUncheckedProperty<T>(private val raw: Any?) {
    @Suppress("UNCHECKED_CAST")
    val current: T get() = raw as T
}

private class PublicPropertyInt : PublicUncheckedProperty<Int>(40)
private val publicPropertyTotal: Int = PublicPropertyInt().current + 2

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
    fun directInheritedUncheckedPropertyProjectsItsConcreteResult() {
        val value = ProtectedPropertyInt()
        assertEquals(42, value.constructed)
        assertEquals(42, value.add(2))
        assertEquals(-40, value.negate())
        assertEquals(4, ProtectedPropertyText().length())
        assertEquals(42, ProtectedPropertyNullable(40).add(2))
        assertEquals(2, ProtectedPropertyNullable(null).add(2))
        assertEquals(42, ProtectedPropertyOuter().Built().result)
        assertEquals(42, ProtectedPropertyCopy(ProtectedPropertyCopy(40), 2).read())
        assertEquals(42, ProtectedPropertyStatic.total)
        assertEquals(42, PublicPropertyInt().current + 2)
        assertEquals(42, publicPropertyTotal)
    }

    @TestAttribute
    fun directInheritedUncheckedPropertyPreservesDeferredGenericStorage() {
        assertEquals(7, ProtectedPropertyDeferred<Int>(null).initialized)
        assertEquals(42, ProtectedPropertyDeferred<Int>(42).initialized)
        assertEquals(7, ProtectedPropertyDeferred<Int>(null).consume(false))
        assertEquals(42, ProtectedPropertyDeferred<Int>(42).consume(true))
        assertEquals(0, ProtectedPropertyDeferred<Int>(null).consume(true))
        assertEquals(7, ProtectedPropertyIgnored().read())
        assertEquals(7, ProtectedPropertyIgnoredText().read())
    }

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
    fun nestedClosureCastDoesNotAlterEnclosingValue() {
        assertEquals(41, throughNestedClosure(41, null, false))
        assertEquals(42, throughNestedClosure(42, 7, true))

        val method = Type.GetType("UncheckedGenericCastReturnTestsKt")!!
            .GetMethod("throughNestedClosure")!!
        assertEquals("System.Object", method.ReturnType.FullName)
    }

    @TestAttribute
    fun nestedSamCastDoesNotAlterEnclosingValue() {
        assertEquals(41, throughNestedSam(41, null, false))
        assertEquals(42, throughNestedSam(42, 7, true))

        val method = Type.GetType("UncheckedGenericCastReturnTestsKt")!!
            .GetMethod("throughNestedSam")!!
        assertEquals("System.Object", method.ReturnType.FullName)
    }
}
