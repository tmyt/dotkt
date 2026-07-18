// #143: decodeToString/encodeToByteArray must honor throwOnInvalidSequence=true by throwing
// CharacterCodingException on malformed UTF-8 / unpaired surrogates, not silently substituting U+FFFD.
import kotlin.text.CharacterCodingException

fun main() {
    val bad = byteArrayOf(0x41, 0xFF.toByte(), 0x42)  // 'A', invalid byte, 'B'

    // Default (throwOnInvalidSequence=false): replacement, no throw.
    println(bad.decodeToString().contains('A'))  // true

    // throwOnInvalidSequence=true: must throw CharacterCodingException.
    try {
        bad.decodeToString(0, bad.size, throwOnInvalidSequence = true)
        println("decode-no-throw")
    } catch (e: CharacterCodingException) {
        println("decode-threw")
    }

    // Encoding an unpaired high surrogate with throwOnInvalidSequence=true must throw.
    // (Built at runtime via Char(0xD800): a lone surrogate cannot survive a string literal.)
    val s = "A" + Char(0xD800) + "B"
    try {
        s.encodeToByteArray(0, s.length, throwOnInvalidSequence = true)
        println("encode-no-throw")
    } catch (e: CharacterCodingException) {
        println("encode-threw")
    }

    // Valid text round-trips unchanged on both paths.
    println("hello".encodeToByteArray().decodeToString())  // hello
}
