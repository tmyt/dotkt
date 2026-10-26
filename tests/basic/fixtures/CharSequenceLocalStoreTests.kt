import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue

// This local implementation keeps CharSequence polymorphic in this assembly.
// A fixture importing only a foreign implementer could instead collapse its locals to String.
private class LocalStoreText(private val text: String) : CharSequence {
    override val length: Int get() = text.length
    override fun get(index: Int): Char = text[index]
    override fun subSequence(startIndex: Int, endIndex: Int): CharSequence =
        LocalStoreText(text.substring(startIndex, endIndex))
    override fun toString(): String = text
}

private inline fun selectLocalText(value: CharSequence, useEmpty: () -> Boolean): CharSequence {
    if (useEmpty()) return ""
    return value
}

private var localTextValueCalls = 0
private fun localTextValue(): String { localTextValueCalls++; return "replacement" }
private fun nullableLocalTextValue(value: String?): String? = value
private fun useStringBranch(): Boolean = true
private fun localTextLength(value: CharSequence): Int = value.length
private fun nullableLocalTextLength(value: CharSequence?): Int = value?.length ?: -1

class CharSequenceLocalStoreTests {
    @TestAttribute
    fun conditionalLocalStoresAndInlineResultsRetainCharSequenceRepresentation() {
        val original: CharSequence = LocalStoreText("original")
        var text: CharSequence = original
        localTextValueCalls = 0
        text = if (useStringBranch()) localTextValue() else original
        assertEquals(11, localTextLength(text))
        assertEquals(1, localTextValueCalls)
        text = if (useStringBranch()) original else localTextValue()
        assertTrue(text === original)
        assertEquals(0, localTextLength(selectLocalText(original) { true }))
        assertTrue(selectLocalText(original) { false } === original)

        var nullable: CharSequence? = original
        nullable = if (useStringBranch()) nullableLocalTextValue("present") else original
        assertEquals(7, nullableLocalTextLength(nullable))
        nullable = if (useStringBranch()) nullableLocalTextValue(null) else original
        assertEquals(-1, nullableLocalTextLength(nullable))
        nullable = original
        assertTrue(nullable === original)

        assertEquals(0, localTextLength(LocalStoreText(" \t").trimStart()))
        assertEquals(0, localTextLength(LocalStoreText(" \t").trimEnd()))
    }
}
