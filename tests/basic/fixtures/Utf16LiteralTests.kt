import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private const val utf16HighConstant: Char = '\ud800'
private const val utf16LowConstant: String = "\udfff"
private fun utf16CharCode(value: Char): Int = value.code
private fun utf16DefaultChar(value: Char = '\udbff'): Int = value.code
private fun utf16DefaultText(value: String = "\udc00"): String = value
private fun assertExactLiteralUnits(text: String, vararg expected: Int) {
    assertEquals(expected.size, text.length)
    for (i in expected.indices) assertEquals(expected[i], text[i].code)
}

class Utf16LiteralTests {
    @TestAttribute
    fun literalsConstantsAndDefaultsRetainExactCodeUnits() {
        val chars = charArrayOf('\u0000', '\ud7ff', '\ud800', '\udbff', '\udc00', '\udfff', '\ue000', '\uffff')
        val expected = intArrayOf(0, 0xd7ff, 0xd800, 0xdbff, 0xdc00, 0xdfff, 0xe000, 0xffff)
        for (i in chars.indices) {
            assertEquals(expected[i], utf16CharCode(chars[i]))
            assertEquals(expected[i], expected[i].toChar().code)
        }
        assertExactLiteralUnits("\ud800", 0xd800)
        assertExactLiteralUnits("\udbff", 0xdbff)
        assertExactLiteralUnits("\udc00", 0xdc00)
        assertExactLiteralUnits("\udfff", 0xdfff)
        assertExactLiteralUnits("\udfff\ud800", 0xdfff, 0xd800)
        assertExactLiteralUnits("\ud800\ud800", 0xd800, 0xd800)
        assertExactLiteralUnits("\udfff\udfff", 0xdfff, 0xdfff)
        assertExactLiteralUnits("\ud83d\ude00", 0xd83d, 0xde00)
        assertExactLiteralUnits("\u0000\"\\\u20ac\ud800A\udfff\ud83d\ude00", 0, 34, 92, 0x20ac, 0xd800, 65, 0xdfff, 0xd83d, 0xde00)
        assertEquals(0xd800, utf16HighConstant.code)
        assertExactLiteralUnits(utf16LowConstant, 0xdfff)
        assertEquals(0xdbff, utf16DefaultChar())
        assertExactLiteralUnits(utf16DefaultText(), 0xdc00)
        assertExactLiteralUnits("")
        assertExactLiteralUnits("ordinary", 111, 114, 100, 105, 110, 97, 114, 121)
    }
}
