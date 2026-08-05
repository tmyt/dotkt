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
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
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

    @TestAttribute
    fun utf8ThrowOnInvalid() {
        val bad = byteArrayOf(0x41, 0xFF.toByte(), 0x42)   // 'A', invalid byte, 'B'
        assertTrue(bad.decodeToString().contains('A'))     // true (default: replacement, no throw)
        var decodeThrew = false
        try {
            bad.decodeToString(0, bad.size, throwOnInvalidSequence = true)
        } catch (e: CharacterCodingException) { decodeThrew = true }
        assertTrue(decodeThrew)                            // decode-threw
        val s = "A" + Char(0xD800) + "B"                   // lone high surrogate
        var encodeThrew = false
        try {
            s.encodeToByteArray(0, s.length, throwOnInvalidSequence = true)
        } catch (e: CharacterCodingException) { encodeThrew = true }
        assertTrue(encodeThrew)                            // encode-threw
        assertEquals("hello", "hello".encodeToByteArray().decodeToString())  // hello (valid round-trips)
    }
}
