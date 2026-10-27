package roundtrip.upstreamstdlib

class BoundarySequence(private val text: String) : CharSequence {
    override val length: Int get() = text.length
    override fun get(index: Int): Char = text[index]
    override fun subSequence(startIndex: Int, endIndex: Int): CharSequence =
        BoundarySequence(text.substring(startIndex, endIndex))
    override fun toString(): String = "rendered"
}

inline fun <R> inlineSequenceIndex(value: CharSequence, predicate: (Char) -> Boolean, block: (Int) -> R): R {
    var found = -1
    for (index in 0 until value.length) {
        if (predicate(value[index])) { found = index; break }
    }
    return block(found)
}

fun sequenceIndexInLibrary(value: CharSequence, predicate: (Char) -> Boolean): Int =
    inlineSequenceIndex(value, predicate) { it }

fun sequenceObjectInLibrary(flag: Boolean, value: CharSequence): Any = if (flag) "raw" else value
fun sequenceElvisInLibrary(value: CharSequence?): String = (value ?: "raw").toString()
