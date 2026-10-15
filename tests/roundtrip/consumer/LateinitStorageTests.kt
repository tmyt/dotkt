import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse
import roundtrip.lateinitstorage.LateinitBox
import roundtrip.lateinitstorage.exportedLateinit
import roundtrip.lateinitstorage.exportedLateinitInitialized

class LateinitStorageTests {
    @TestAttribute
    fun genericWritesAcrossDllPreserveStateAndTypedReads() {
        val first = LateinitBox<Int>()
        val second = LateinitBox<Int>()
        assertFalse(first.initialized())
        var threw = false
        try { first.value } catch (error: Exception) { threw = true }
        assertTrue(threw)
        second.value = 0
        assertFalse(first.initialized())
        assertTrue(first.other(second))
        assertEquals(0, second.value)
        val text = LateinitBox<String>()
        assertFalse(text.initialized())
        text.value = ""
        assertTrue(text.initialized())
        assertEquals("", text.value)
    }

    @TestAttribute
    fun callableReferenceWritesAcrossDllUseTheSameProperty() {
        val box = LateinitBox<Int>()
        val producerReference = box.reference()
        producerReference.set(0)
        assertTrue(box.initialized())
        val consumerReference = box::value
        consumerReference.set(17)
        assertEquals(17, producerReference.get())
        val unbound = LateinitBox<Int>::value
        unbound.set(box, 23)
        assertEquals(23, consumerReference.get())
    }

    @TestAttribute
    fun topLevelWritesAcrossDllUseTypedAccessors() {
        assertFalse(exportedLateinitInitialized())
        exportedLateinit = "consumer"
        assertTrue(exportedLateinitInitialized())
        assertEquals("consumer", exportedLateinit)
    }
}
