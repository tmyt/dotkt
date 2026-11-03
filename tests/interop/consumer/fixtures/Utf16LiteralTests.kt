import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import Utf16LiteralInterop.Api

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
        assertEquals(0xdbff, Api.DefaultChar().code)
        assertEquals(0xdc00, Api.DefaultText()[0].code)
    }
}
