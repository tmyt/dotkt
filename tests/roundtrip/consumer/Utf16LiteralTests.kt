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
    @TestAttribute fun importedSlicesSeparateRawStorageFromTranscoding() {
        val chars = charArrayOf('!', '\ud800', '\u0000', '\udfff', '?')
        val raw = textWindow(chars, 1, 4)
        chars[1] = 'A'
        assertImportedUnits(raw, 0xd800, 0, 0xdfff)
        check(textWindow(chars, chars.size, chars.size).isEmpty())
        val text = "\ud800\udc00"
        val bytes = encodedWindow(text, 1, 2)
        check(bytes.contentEquals(byteArrayOf(0xef.toByte(), 0xbf.toByte(), 0xbd.toByte())))
        val padded = byteArrayOf(0xff.toByte(), 0xc2.toByte(), 0x80.toByte(), 0xff.toByte())
        val decoded = decodedWindow(padded, 1, 3)
        padded[2] = 65
        assertImportedUnits(decoded, 0x80)
        try { decodedWindow(padded, 1, 2); error("Expected strict slice failure") }
        catch (_: CharacterCodingException) {}
    }

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
