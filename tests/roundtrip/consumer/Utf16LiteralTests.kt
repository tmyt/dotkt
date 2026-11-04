package roundtriptests.utf16literals

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.utf16literals.*

private const val IMPORTED_TEXT = LOW_TEXT + Utf16ConstantOwner.TEXT

private fun assertImportedUnits(text: String, vararg expected: Int) {
    assertEquals(expected.size, text.length)
    for (i in expected.indices) assertEquals(expected[i], text[i].code)
}

class Utf16LiteralTests {
    @TestAttribute
    fun dllLiteralBodiesAndDefaultsPreserveCodeUnits() {
        assertEquals(0xd800, HIGH_CHAR.code)
        assertImportedUnits(LOW_TEXT, 0xdfff)
        assertEquals(3, "abc".LOW_TEXT)
        assertImportedUnits(Utf16ConstantOwner.TEXT, 0xd800, 0, 0xdfff, 0xd83d, 0xde00)
        assertImportedUnits(IMPORTED_TEXT, 0xdfff, 0xd800, 0, 0xdfff, 0xd83d, 0xde00)
        assertImportedUnits(readConstantText(), 0xdfff)
        assertEquals(0xdbff, directChar().code)
        assertImportedUnits(directText(), 0xdc00, 88, 0xd800, 0xd83d, 0xde00)
        assertEquals(0xd800, inlineChar().code)
        assertImportedUnits(inlineText(), 0xdfff, 0xd800)
        assertEquals(0xdbff, defaultChar().code)
        assertImportedUnits(defaultText(), 0xdc00)
    }
}
