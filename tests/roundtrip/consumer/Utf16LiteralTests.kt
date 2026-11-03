package roundtriptests.utf16literals

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.utf16literals.*

private fun assertImportedUnits(text: String, vararg expected: Int) {
    assertEquals(expected.size, text.length)
    for (i in expected.indices) assertEquals(expected[i], text[i].code)
}

class Utf16LiteralTests {
    @TestAttribute
    fun dllLiteralBodiesAndDefaultsPreserveCodeUnits() {
        assertEquals(0xd800, HIGH_CHAR.code)
        assertImportedUnits(readConstantText(), 0xdfff)
        assertEquals(0xdbff, directChar().code)
        assertImportedUnits(directText(), 0xdc00, 88, 0xd800, 0xd83d, 0xde00)
        assertEquals(0xd800, inlineChar().code)
        assertImportedUnits(inlineText(), 0xdfff, 0xd800)
        assertEquals(0xdbff, defaultChar().code)
        assertImportedUnits(defaultText(), 0xdc00)
    }
}
