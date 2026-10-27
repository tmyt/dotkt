package roundtrip.representationtypes

class RepresentationText(private val text: String) : CharSequence {
    override val length: Int get() = text.length
    override fun get(index: Int): Char = text[index]
    override fun subSequence(startIndex: Int, endIndex: Int): CharSequence =
        RepresentationText(text.substring(startIndex, endIndex))
    override fun toString(): String = "rendered"
}

fun representationAt(value: CharSequence, index: Int): Char = value[index]
fun echoRepresentation(value: CharSequence): CharSequence = value
fun nullableRepresentation(value: CharSequence?): CharSequence? = value
fun <T> genericRepresentation(value: T, text: CharSequence): T {
    check(text.length > 0)
    return value
}
fun <T : CharSequence> boundedRepresentation(value: T): T = value
class RepresentationBox<T : CharSequence>(val value: T)
fun boxedRepresentation(value: RepresentationBox<*>): Any? = value.value
fun nativeRepresentation(value: System.Version): System.Version = value
