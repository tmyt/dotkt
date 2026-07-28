import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert
import roundtrip.valueclass.tokenOf
import roundtrip.valueclass.tokenValue

class ValueClassRoundtripTests {
    @TestAttribute
    fun nonPublicUnderlyingPropertyMetadata() {
        val token = tokenOf(21)
        ClassicAssert.AreEqual(42, token.doubled())
        ClassicAssert.AreEqual(42, tokenValue(token))
    }
}
