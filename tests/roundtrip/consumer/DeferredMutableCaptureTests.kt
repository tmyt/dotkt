package roundtrip.deferredcapturetests

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.deferredcapture.deferredReader
import roundtrip.deferredcapture.readerFromUpdatedLocal

class DeferredMutableCaptureTests {
    @TestAttribute
    fun importedInlineReaderSharesConsumerVariable() {
        assertEquals("changed", consumerRead("initial", "changed"))
        assertEquals(2, consumerRead(1, 2))
    }

    @TestAttribute
    fun importedInlineLocalRetainsSharedStorage() {
        assertEquals("changed!", readerFromUpdatedLocal("initial", "changed") { it + "!" }())
        assertEquals(3, readerFromUpdatedLocal(1, 2) { it + 1 }())
    }
}

private fun <T> consumerRead(initial: T, next: T): T {
    var current = initial
    val read = deferredReader { current }
    current = next
    return read()
}
