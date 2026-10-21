package roundtrip.consumer

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.enumparsename.ParseMode
import roundtrip.enumparsename.producerParse
import roundtrip.enumparsename.producerReifiedParse

private enum class ConsumerParseMode { FIRST, SECOND }
private fun importedParseFails(action: () -> Unit): Boolean {
    try { action() } catch (e: IllegalArgumentException) { return true }
    return false
}

class EnumParseNameRoundtripTests {
    @TestAttribute
    fun producerAndConsumerShareTheExactNameContract() {
        assertEquals(ParseMode.SECOND, producerParse("SECOND"))
        assertEquals(ParseMode.SECOND, ParseMode.valueOf("SECOND"))
        for (invalid in listOf("1", " FIRST", "first", "FIRST, SECOND")) {
            assertTrue(importedParseFails { producerParse(invalid) })
            assertTrue(importedParseFails { ParseMode.valueOf(invalid) })
        }
    }

    @TestAttribute
    fun importedReifiedBodiesParseLocalAndReferencedDeclarations() {
        assertEquals(ConsumerParseMode.FIRST, producerReifiedParse<ConsumerParseMode>("FIRST"))
        assertEquals(ParseMode.SECOND, producerReifiedParse<ParseMode>("SECOND"))
        assertTrue(importedParseFails { producerReifiedParse<ConsumerParseMode>("1") })
        assertTrue(importedParseFails { producerReifiedParse<ParseMode>("1") })
    }
}
