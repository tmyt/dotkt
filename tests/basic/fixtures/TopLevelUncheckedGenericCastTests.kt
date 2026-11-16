package unchecked.top

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue

@Suppress("UNCHECKED_CAST")
private fun <T> read(raw: Any?): T = raw as T

@Suppress("UNCHECKED_CAST")
private fun <T> erasedObject(raw: Any?): Any? = raw as T

@Suppress("UNCHECKED_CAST")
private fun <T> erasedSafeObject(raw: Any?): Any? = raw as? T

@Suppress("UNCHECKED_CAST", "NOTHING_TO_INLINE")
private inline fun <T> erasedInlineObject(raw: Any?): Any? = raw as T

@Suppress("UNCHECKED_CAST")
private fun <T> erasedDefaultObject(raw: Any?, result: Any? = raw as T): Any? = result

@Suppress("UNCHECKED_CAST")
private fun <T> erasedClosureObject(raw: Any?): Any? = run { raw as T }

private open class CastBound
private class CastDerived : CastBound()
private interface CastMarker<T>
private class TextCastMarker : CastMarker<String>

@Suppress("UNCHECKED_CAST")
private fun <T : CastBound> erasedBoundObject(raw: Any?): Any? = raw as T

@Suppress("UNCHECKED_CAST")
private fun <T : CastMarker<Int>> erasedMarkerObject(raw: Any?): Any? = raw as? T

private class CastOwner<A : CastBound> {
    @Suppress("UNCHECKED_CAST")
    fun <B> erase(raw: Any?): Any? = raw as B
}

private inline fun <reified T> checkedObject(raw: Any?): Any? = raw as T

private fun <T> deferred(seed: T, consume: Boolean, raw: Any?): Int {
    val value = read<T>(raw)
    if (!consume) return 7
    return value.hashCode()
}

private fun <T> ordinary(seed: T, consume: Boolean, raw: Any?): Int {
    val value = readOrdinaryTop<T>(raw)
    if (!consume) return 7
    return value.hashCode()
}

@Suppress("UNCHECKED_CAST")
private fun <T> overloaded(raw: Any?, seed: T): T = raw as T

private fun <T> overloaded(raw: String, seed: T): T = seed

private fun <T> deferredOverload(seed: T, consume: Boolean, raw: Any?): Int {
    val value = overloaded(raw, seed)
    if (!consume) return 7
    return value.hashCode()
}

private fun <T> ordinaryOverload(seed: T): Int {
    val value = overloaded("ignored", seed)
    return value.hashCode()
}

private fun consumeInt(value: Int): Int = value + 1

private fun concrete(raw: Any?): Int = consumeInt(read<Int>(raw))

private class StaticUncheckedReader<T> {
    companion {
        @Suppress("UNCHECKED_CAST")
        fun <R> read(raw: Any?): R = raw as R
    }
}

private fun <T> deferredStatic(seed: T, consume: Boolean, raw: Any?): Int {
    val value = StaticUncheckedReader.read<T>(raw)
    if (!consume) return 7
    return value.hashCode()
}

private fun <T> identity(value: T): T = value

private fun <T> forwarded(seed: T, consume: Boolean, raw: Any?): T {
    val value = read<T>(raw)
    if (!consume) return seed
    return identity(value)
}

class TopLevelUncheckedGenericCastTests {
    @TestAttribute
    fun privateTopLevelResultRemainsErasedForUnusedAndObjectUses() {
        assertEquals(7, deferred(0, false, null))
        assertEquals(7, deferred(0, false, "wrong"))
        assertEquals(0, deferred(0, true, null))
        assertEquals("wrong".hashCode(), deferred(0, true, "wrong"))
        assertEquals(42, deferred(0, true, 42))
        assertEquals("wrong", erasedObject<Int>("wrong"))
        assertEquals(null, erasedObject<Int>(null))
        assertEquals(42, erasedObject<String>(42))
        assertEquals("wrong", erasedSafeObject<Int>("wrong"))
        assertEquals(null, erasedSafeObject<Int>(null))
        assertEquals("wrong", erasedInlineObject<Int>("wrong"))
        assertEquals("wrong", erasedDefaultObject<Int>("wrong"))
        assertEquals("explicit", erasedDefaultObject<Int>("wrong", "explicit"))
        assertEquals("wrong", erasedClosureObject<Int>("wrong"))
        assertEquals("wrong", CastOwner<CastDerived>().erase<Int>("wrong"))
        val derived = CastDerived()
        assertTrue(erasedBoundObject<CastDerived>(derived) === derived)
        val base = CastBound()
        assertTrue(erasedBoundObject<CastDerived>(base) === base)
        val marker = TextCastMarker()
        assertTrue(erasedMarkerObject<CastMarker<Int>>(marker) === marker)
        assertEquals(null, erasedMarkerObject<CastMarker<Int>>("wrong"))
        var evaluations = 0
        assertEquals("once", erasedInlineObject<Int>(run { evaluations++; "once" }))
        assertEquals(1, evaluations)
    }

    @TestAttribute
    fun ordinaryCrossFileResultRemainsErasedForUnusedAndObjectUses() {
        assertEquals(7, ordinary(0, false, null))
        assertEquals(7, ordinary(0, false, "wrong"))
        assertEquals(0, ordinary(0, true, null))
        assertEquals("wrong".hashCode(), ordinary(0, true, "wrong"))
        assertEquals(42, ordinary(0, true, 42))
    }

    @TestAttribute
    fun exactSelectedOverloadControlsThePhysicalResult() {
        assertEquals(7, deferredOverload(0, false, null))
        assertEquals(7, deferredOverload(0, false, "wrong"))
        assertEquals("wrong".hashCode(), deferredOverload(0, true, "wrong"))
        assertEquals(42, ordinaryOverload(42))
    }

    @TestAttribute
    fun concreteConsumptionStillChecksAtItsUse() {
        assertEquals(43, concrete(42))
        var failed = false
        try {
            concrete("wrong")
        } catch (_: System.InvalidCastException) {
            failed = true
        }
        assertTrue(failed)
        failed = false
        try {
            checkedObject<Int>("wrong")
        } catch (_: System.InvalidCastException) {
            failed = true
        }
        assertTrue(failed)
        failed = false
        try {
            System.Math.Abs(read<Int>("wrong"))
        } catch (_: System.InvalidCastException) {
            failed = true
        }
        assertTrue(failed)
        assertEquals(42, System.Math.Abs(read<Int>(-42)))
        failed = false
        try {
            erasedBoundObject<CastDerived>("wrong")
        } catch (_: System.InvalidCastException) {
            failed = true
        }
        assertTrue(failed)
    }

    @TestAttribute
    fun staticCarrierResultRemainsErasedForUnusedAndObjectUses() {
        assertEquals(7, deferredStatic(0, false, null))
        assertEquals(7, deferredStatic(0, false, "wrong"))
        assertEquals(0, deferredStatic(0, true, null))
        assertEquals("wrong".hashCode(), deferredStatic(0, true, "wrong"))
        assertEquals(42, deferredStatic(0, true, 42))
    }

    @TestAttribute
    fun deferredTopLevelResultCanEnterStaticArgumentsAndReturns() {
        assertEquals(0, forwarded(0, false, null))
        assertEquals(0, forwarded(0, false, "wrong"))
        assertEquals("seed", forwarded("seed", false, 42))
        assertEquals(42, forwarded(0, true, 42))
        assertEquals("value", forwarded("", true, "value"))
    }
}
