import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private class DeferredNullableValue<T>(val value: T)

private class DeferredStorageReader<T>(private val seed: T) {
    @Suppress("UNCHECKED_CAST")
    private fun read(raw: Any?): T = raw as T

    private fun consume(value: T): Int = value.hashCode()

    fun readWhenRequested(use: Boolean, raw: Any?): Int {
        val value = read(raw)
        if (!use) return 7
        return consume(value)
    }

    fun nullableValue(value: DeferredNullableValue<T?>): T? = value.value
}

private class DeferredMethodStorageReader {
    @Suppress("UNCHECKED_CAST")
    private fun <T> read(raw: Any?): T = raw as T

    private fun <T> consume(value: T): Int = value.hashCode()

    // The value parameter demands the caller's method-scoped storage companion.
    fun <T> readWhenRequested(seed: T, use: Boolean, raw: Any?): Int {
        val value = read<T>(raw)
        if (!use) return 7
        return consume(value)
    }
}

class DeferredStorageResultTests {
    @TestAttribute
    fun unusedNullPrimitiveResultKeepsObjectStorage() {
        assertEquals(7, DeferredStorageReader(0).readWhenRequested(false, null))
    }

    @TestAttribute
    fun unusedWrongPrimitiveResultKeepsObjectStorage() {
        assertEquals(7, DeferredStorageReader(0).readWhenRequested(false, "not an Int"))
    }

    @TestAttribute
    fun consumedPrimitiveResultIsNarrowedAtItsTypedArgument() {
        assertEquals(42, DeferredStorageReader(0).readWhenRequested(true, 42))
        var failed = false
        try {
            DeferredStorageReader(0).readWhenRequested(true, "not an Int")
        } catch (_: System.InvalidCastException) {
            failed = true
        }
        check(failed)
    }

    @TestAttribute
    fun unusedReferenceResultDoesNotAcquireAnEarlyClassCast() {
        assertEquals(7, DeferredStorageReader("").readWhenRequested(false, null))
        assertEquals(7, DeferredStorageReader("").readWhenRequested(false, 42))
    }

    @TestAttribute
    fun consumedReferenceResultRetainsItsTypedArgumentCheck() {
        assertEquals("value".hashCode(), DeferredStorageReader("").readWhenRequested(true, "value"))
        var failed = false
        try {
            DeferredStorageReader("").readWhenRequested(true, 42)
        } catch (_: System.InvalidCastException) {
            failed = true
        }
        check(failed)
    }

    @TestAttribute
    fun methodStorageCompanionDoesNotForceAnUnusedUnbox() {
        val reader = DeferredMethodStorageReader()
        assertEquals(7, reader.readWhenRequested(0, false, null))
        assertEquals(7, reader.readWhenRequested("", false, 42))
        assertEquals(42, reader.readWhenRequested(0, true, 42))
    }

    @TestAttribute
    fun nullableCompanionKeepsItsActualGenericStackSlot() {
        val reader = DeferredStorageReader(0)
        assertEquals(null, reader.nullableValue(DeferredNullableValue<Int?>(null)))
        assertEquals(42, reader.nullableValue(DeferredNullableValue<Int?>(42)))
    }
}
