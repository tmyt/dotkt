package roundtrip.charsequencesupertypes

interface Slot<T> { fun read(): T }

class Text : Slot<CharSequence> {
    override fun read(): CharSequence = "ok"
}

class NullableText(private val present: Boolean = false) : Slot<CharSequence?> {
    var reads = 0
    override fun read(): CharSequence? {
        reads++
        return if (present) "nullable" else null
    }
}

class NestedText : Slot<Slot<CharSequence>> {
    override fun read(): Slot<CharSequence> = Text()
}

open class Base<T>(val value: T)
class DerivedText(value: String = "base") : Base<CharSequence>(value)

class UnchangedText : Slot<String> {
    override fun read(): String = "unchanged"
}

interface PairSlot<A, B> { fun left(): A; fun right(): B }
interface Marker<T>
class MixedText : PairSlot<CharSequence, Int?>, Marker<String> {
    override fun left(): CharSequence = "mixed"
    override fun right(): Int? = null
}
