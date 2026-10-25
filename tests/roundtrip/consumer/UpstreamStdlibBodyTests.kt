import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.time.Duration
import roundtrip.upstreamstdlib.TextSequence
import roundtrip.upstreamstdlib.padInLibrary
import roundtrip.upstreamstdlib.parseNanosInLibrary
import roundtrip.upstreamstdlib.whitespacePredicate
import roundtrip.upstreamstdlib.withTrimmedText

private fun nonLocalTrimmedResult(text: CharSequence): String {
    withTrimmedText(text) { return it.toString() }
}

private inline fun selectText(value: CharSequence, useEmpty: () -> Boolean): CharSequence {
    if (useEmpty()) return ""
    return value
}

private var textValueCalls = 0
private fun textValue(): String { textValueCalls++; return "replacement" }
private fun nullableTextValue(value: String?): String? = value

class UpstreamStdlibBodyTests {
    @TestAttribute
    fun stringTrimAndCharDefaultsUseRestoredStdlibBodies() {
        assertEquals("text", "xytextyx".trim('x', 'y'))
        assertEquals("textyx", "xytextyx".trimStart('x', 'y'))
        assertEquals("xytext", "xytextyx".trimEnd('x', 'y'))
        assertEquals("", "xxxxx".trim('x'))
        assertEquals("", "xxxxx".trimStart('x'))
        assertEquals("", "xxxxx".trimEnd('x'))
        assertEquals("", "".trim('x'))
        assertEquals("", "".trimStart('x'))
        assertEquals("", "".trimEnd('x'))
        assertEquals("text", "text".trim())
        assertEquals("  x", "x".padStart(3))
        assertEquals("x  ", "x".padEnd(3))
        assertEquals("00x", "x".padStart(3, '0'))
        assertEquals("x00", "x".padEnd(3, '0'))
        assertEquals("text", "text".padStart(2))
        assertEquals("text", "text".padEnd(2))
        assertEquals("  x", padInLibrary("x", width = 3))
        assertEquals("..x", padInLibrary("x", width = 3, padding = '.'))
        var rejected = false
        try { "x".padEnd(-1) } catch (e: IllegalArgumentException) { rejected = true }
        assertTrue(rejected)
    }

    @TestAttribute
    fun customCharSequenceTrimUsesWhitespaceCallableReferences() {
        val text: CharSequence = TextSequence("\t \u2003text\n ")
        assertEquals("text", text.trim().toString())
        assertEquals("text\n ", text.trimStart().toString())
        assertEquals("\t \u2003text", text.trimEnd().toString())
        assertEquals("", TextSequence(" \t\n").trimStart().toString())
        assertEquals("", TextSequence(" \t\n").trimEnd().toString())
        assertEquals("  x", TextSequence("x").padStart(3).toString())
        assertEquals("x..", TextSequence("x").padEnd(3, '.').toString())
        val isWhitespace = whitespacePredicate()
        assertTrue(isWhitespace('\u2003'))
        assertFalse(isWhitespace('x'))
        assertEquals("text", nonLocalTrimmedResult(text))
    }

    @TestAttribute
    fun blankChecksIterateStringsAndUserCharSequences() {
        assertTrue("".isBlank())
        assertTrue(" \t\n\u2003".isBlank())
        assertFalse(" \tx\n".isBlank())
        assertTrue(TextSequence("").isBlank())
        assertTrue(TextSequence(" \t\n\u2003").isBlank())
        assertFalse(TextSequence(" \tx\n").isBlank())
    }

    @TestAttribute
    fun durationFractionsUseOriginalInlineMemberExtensionParser() {
        assertEquals(1_250_000_000L, parseNanosInLibrary("1.25s"))
        assertEquals(-1_250_000_000L, Duration.parse("-1.25s").inWholeNanoseconds)
        assertEquals(1_234_567_890L, Duration.parse("1.234567890123456789s").inWholeNanoseconds)
        assertEquals(1L, Duration.parse("0.000000001s").inWholeNanoseconds)
        assertEquals(0L, Duration.parse("0.0000000001s").inWholeNanoseconds)
        assertEquals(62_003_000_000L, Duration.parse("1m 2.003s").inWholeNanoseconds)
        assertEquals(3_723_004_005_006L, Duration.parseIsoString("PT1H2M3.004005006S").inWholeNanoseconds)
        assertEquals(null, Duration.parseOrNull("1.2.3s"))
    }

    @TestAttribute
    fun charSequenceStoresAdaptReassignmentAndInlineEarlyReturns() {
        val original: CharSequence = TextSequence("original")
        var text: CharSequence = original
        textValueCalls = 0
        text = textValue()
        assertEquals("replacement", text.toString())
        assertEquals(1, textValueCalls)
        text = original
        assertTrue(text === original)
        assertEquals("", selectText(original) { true }.toString())
        assertTrue(selectText(original) { false } === original)

        var nullable: CharSequence? = original
        nullable = nullableTextValue("present")
        assertEquals("present", nullable.toString())
        nullable = nullableTextValue(null)
        assertEquals(null, nullable)
    }
}
