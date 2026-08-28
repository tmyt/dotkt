import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private fun interopNullableCharSequence(value: String?): CharSequence? = value
private fun interopCharSequence(value: String): CharSequence = value
private fun concreteAppendRange(value: CharSequence?): String =
    StringBuilder().append(value, 1, 3).toString()
private fun referencedCharSequenceAsAny(): Any = interopCharSequence(" abcd ").trim()

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

        val directConcrete = StringBuilder()
        directConcrete.append(interopNullableCharSequence("abcd"), 1, 3)
        assertEquals("bc", directConcrete.toString())
        assertEquals("bc", concreteAppendRange(interopNullableCharSequence("abcd")))
        assertEquals("x", interopCharSequence(" x ").trim().toString())

        val opaque = referencedCharSequenceAsAny()
        assertEquals(
            "bc",
            if (opaque is CharSequence) StringBuilder().append(opaque, 1, 3).toString()
            else "not-a-char-sequence",
        )

        val absent = interopNullableCharSequence(null)
        val nullThroughInterface: Appendable = StringBuilder()
        nullThroughInterface.append(absent, 0, 4)
        assertEquals("null", nullThroughInterface.toString())

        val nullThroughConcrete = StringBuilder()
        nullThroughConcrete.append(absent, 0, 4)
        assertEquals("null", nullThroughConcrete.toString())
    }
}
