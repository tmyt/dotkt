// Stdlib-binding battery: `use {}` on AutoCloseable and UTF-8 throw-on-invalid. Migrates that family of
// cases/il-* onto the in-process NUnit suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute
// method; every asserted value is preserved 1:1 (see `// <expected>`). il-use's ORDERED side-effecting `close()`
// prints (the actual subject: close runs in `finally`, before/around the block value) become a captured log list
// asserted directly, so the try/finally ordering that was the point is unchanged.
//
// Coverage preserved (old case -> method):
//   il-use      -> useCloseable        `use {}` -> try{block(it)}finally{close()}; block value returned; close runs on normal + throw paths
//   il-utf8throw-> utf8ThrowOnInvalid  #143 decodeToString/encodeToByteArray honor throwOnInvalidSequence=true -> CharacterCodingException
//
// All top-level declarations introduced here are ResourceUtf8-prefixed (one assembly = one namespace).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.text.CharacterCodingException

// ---- il-use : close() ordering captured into a log (was ordered `println`s) --------------------------------------
class ResourceUtf8Res(val tag: String, val log: MutableList<String>) : AutoCloseable {
    fun read(): Int = tag.length
    override fun close() { log.add("close " + tag) }
}

class ResourceAndUtf8Tests {
    @TestAttribute
    fun useCloseable() {
        val log = mutableListOf<String>()
        val n = ResourceUtf8Res("abcd", log).use { it.read() }   // block value returned; close runs in finally first
        assertEquals(4, n)                              // n=4
        assertEquals("close abcd", log[0])              // close abcd
        var caught = ""
        try {
            ResourceUtf8Res("x", log).use { throw RuntimeException("boom") }   // close still runs on throw
        } catch (e: Exception) { caught = e.message ?: "" }
        assertEquals("close x", log[1])                 // close x
        assertEquals("boom", caught)                    // caught:boom
    }

    private fun octets(vararg values: Int) = ByteArray(values.size) { values[it].toByte() }

    @TestAttribute fun utf8ScalarBoundariesUseExactBytes() {
        val text = "\u0000\u007f\u0080\u07ff\u0800\ud7ff\ue000\uffff\ud800\udc00\udbff\udfff"
        val bytes = octets(0, 0x7f, 0xc2, 0x80, 0xdf, 0xbf, 0xe0, 0xa0, 0x80, 0xed, 0x9f, 0xbf,
            0xee, 0x80, 0x80, 0xef, 0xbf, 0xbf, 0xf0, 0x90, 0x80, 0x80, 0xf4, 0x8f, 0xbf, 0xbf)
        check(bytes.decodeToString(throwOnInvalidSequence = true) == text)
        check(text.encodeToByteArray(throwOnInvalidSequence = true).contentEquals(bytes))
        check(bytes.decodeToString(bytes.size, bytes.size, true).isEmpty())
        check(text.encodeToByteArray(text.length, text.length, true).isEmpty())
    }

    @TestAttribute fun utf8ThrowOnInvalid() {
        fun malformed(expected: String, vararg values: Int) {
            val bytes = octets(*values)
            check(bytes.decodeToString() == expected)
            try { bytes.decodeToString(throwOnInvalidSequence = true); error("Expected strict decode failure") }
            catch (_: CharacterCodingException) {}
        }
        malformed("A\ufffdB", 65, 0xff, 66)
        malformed("\ufffd\ufffd", 0xc0, 0xaf)                      // overlong two-byte lead
        malformed("\ufffd\ufffd\ufffd", 0xe0, 0x9f, 0x80)         // overlong three-byte form
        malformed("\ufffd\ufffd\ufffd", 0xed, 0xa0, 0x80)         // encoded surrogate
        malformed("\ufffd\ufffd\ufffd\ufffd", 0xf4, 0x90, 0x80, 0x80)
        malformed("\ufffdA\ufffd", 0xe2, 0x82, 65, 0xf0, 0x9f, 0x92)
        val unpaired = "\ud800A\udfff"
        check(unpaired.encodeToByteArray().contentEquals(octets(0xef, 0xbf, 0xbd, 65, 0xef, 0xbf, 0xbd)))
        try { unpaired.encodeToByteArray(throwOnInvalidSequence = true); error("Expected strict encode failure") }
        catch (_: CharacterCodingException) {}
    }

    @TestAttribute fun invalidRangesTakePrecedenceOverMalformedContent() {
        val bytes = octets(0xff)
        try { bytes.decodeToString(0, Int.MAX_VALUE, true); error("Expected bounds failure") }
        catch (_: IndexOutOfBoundsException) {}
        try { "\ud800".encodeToByteArray(Int.MIN_VALUE, 0, true); error("Expected bounds failure") }
        catch (_: IndexOutOfBoundsException) {}
        try { bytes.decodeToString(1, 0, true); error("Expected inverted range failure") }
        catch (_: IllegalArgumentException) {}
        try { charArrayOf('a').concatToString(0, Int.MAX_VALUE); error("Expected bounds failure") }
        catch (_: IndexOutOfBoundsException) {}
    }
}
