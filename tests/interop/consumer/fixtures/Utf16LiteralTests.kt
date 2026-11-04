import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import Utf16LiteralInterop.Api
import Utf16LiteralInterop.CharPayloadAttribute
import Utf16LiteralInterop.StringPayloadAttribute

@CharPayloadAttribute('\ud800', charArrayOf('\ud800', 'X', '\udfff'), Named = '\udfff', NamedArray = charArrayOf('\udfff', '\ud800'))
private class AttributedUtf16Literal

@StringPayloadAttribute("\ud800")
private class AttributedUtf16StringLiteral

private const val NATIVE_IMPORTED_TEXT = Api.Text + "!"

class NativeUtf16LiteralTests {
    @TestAttribute
    fun nativeCharConstantsAndOptionalValuesPreserveCodeUnits() {
        assertEquals(0xd800, Api.High.code)
        assertEquals(0xdfff, Api.Low.code)
        val text = Api.ConstantText()
        assertEquals(3, text.length)
        assertEquals(0xd800, text[0].code)
        assertEquals(88, text[1].code)
        assertEquals(0xdfff, text[2].code)
        val imported = Api.Text
        assertEquals(3, imported.length)
        assertEquals(0xd800, imported[0].code)
        assertEquals(0xdfff, imported[2].code)
        assertEquals("\ud800X\udfff!", NATIVE_IMPORTED_TEXT)
        assertEquals("A\ud83d\ude00\ufffd", Api.ScalarText)
        assertEquals(0xdbff, Api.DefaultChar().code)
        assertEquals(0xdc00, Api.DefaultText()[0].code)
        val pair = Api.DefaultPair()
        assertEquals(2, pair.length)
        assertEquals(0xd83d, pair[0].code)
        assertEquals(0xde00, pair[1].code)
        assertEquals("55296|55296,88,57343|57343|57343,55296", Api.AttributeUnits(AttributedUtf16Literal()))
        assertEquals(Api.CSharpStringAttributeUnit(), Api.StringAttributeUnit(AttributedUtf16StringLiteral()))
    }
}
