package unchecked.top

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue

@Suppress("UNCHECKED_CAST")
private fun <T> read(raw: Any?): T = raw as T

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

class TopLevelUncheckedGenericCastTests {
    @TestAttribute
    fun privateTopLevelResultRemainsErasedForUnusedAndObjectUses() {
        assertEquals(7, deferred(0, false, null))
        assertEquals(7, deferred(0, false, "wrong"))
        assertEquals(0, deferred(0, true, null))
        assertEquals("wrong".hashCode(), deferred(0, true, "wrong"))
        assertEquals(42, deferred(0, true, 42))
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
    }
}
