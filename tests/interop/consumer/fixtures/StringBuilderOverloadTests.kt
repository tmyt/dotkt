import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private fun interopNullableCharSequence(value: String?): CharSequence? = value

class StringBuilderOverloadTests {
    @TestAttribute
    fun pureAppAppendRangePreservesKotlinContract() {
        val text = interopNullableCharSequence("abcd")
        val throughInterface: Appendable = StringBuilder()
        throughInterface.append(text, 1, 3)
        assertEquals("bc", throughInterface.toString())

        // This concrete overload is a referenced Kotlin helper, not the intrinsic Appendable slot above.
        val throughConcrete = StringBuilder()
        throughConcrete.append(text, 1, 3)
        assertEquals("bc", throughConcrete.toString())

        val absent = interopNullableCharSequence(null)
        val nullThroughInterface: Appendable = StringBuilder()
        nullThroughInterface.append(absent, 0, 4)
        assertEquals("null", nullThroughInterface.toString())

        val nullThroughConcrete = StringBuilder()
        nullThroughConcrete.append(absent, 0, 4)
        assertEquals("null", nullThroughConcrete.toString())
    }
}
