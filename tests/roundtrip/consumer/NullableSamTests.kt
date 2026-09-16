package roundtriptests.nullablesam

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.nullablesam.*

private fun <T> importedSam(): Slot<T?> = Slot { it == null }
private fun <T> importedSupplier(value: T?): Supplier<T?> = Supplier { value }

class NullableSamTests {
    @TestAttribute
    fun nullableSamSlotsPreserveDeclarationSubstitution() {
        assertTrue(nullableSam<Int>().inspect(null))
        assertTrue(!nullableSam<Int>().inspect(42))
        assertTrue(nullableSam<String>().inspect(null))
        assertTrue(!nullableSam<String>().inspect("value"))
        assertTrue(declaredNullableSam<Int>().inspect(null))
        assertTrue(!declaredNullableSam<Int>().inspect(42))
        assertEquals(42, nullableSupplier<Int>(42).get())
        assertEquals(null, nullableSupplier<Int>(null).get())
        assertEquals("value", nullableSupplier<String>("value").get())
        val authored: Slot<Int?> = AuthoredNullableSlot<Int>()
        assertTrue(authored.inspect(null))
        assertTrue(!authored.inspect(42))
        assertTrue(importedSam<Int>().inspect(null))
        assertTrue(!importedSam<Int>().inspect(42))
        assertEquals(42, importedSupplier<Int>(42).get())
        assertEquals(null, importedSupplier<Int>(null).get())
    }
}
