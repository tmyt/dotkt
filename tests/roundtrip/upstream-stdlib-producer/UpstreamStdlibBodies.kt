package roundtrip.upstreamstdlib

import kotlin.time.Duration

class TextSequence(private val text: String) : CharSequence {
    override val length: Int get() = text.length
    override fun get(index: Int): Char = text[index]
    override fun subSequence(startIndex: Int, endIndex: Int): CharSequence =
        TextSequence(text.substring(startIndex, endIndex))
    override fun toString(): String = text
}

fun padInLibrary(text: String, width: Int, padding: Char = ' '): String =
    text.padStart(width, padding)

fun whitespacePredicate(): (Char) -> Boolean = Char::isWhitespace

inline fun <R> withTrimmedText(text: CharSequence, block: (CharSequence) -> R): R =
    block(text.trim())

fun parseNanosInLibrary(text: String): Long = Duration.parse(text).inWholeNanoseconds
