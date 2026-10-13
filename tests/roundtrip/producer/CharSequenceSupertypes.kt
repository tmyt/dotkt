package roundtrip.charsequencesupertypes

interface Slot<T> { fun read(): T }

class Text : Slot<CharSequence> {
    override fun read(): CharSequence = "ok"
}

class NullableText : Slot<CharSequence?> {
    override fun read(): CharSequence? = null
}

class NestedText : Slot<Slot<CharSequence>> {
    override fun read(): Slot<CharSequence> = Text()
}

open class Base<T>(val value: T)
class DerivedText : Base<CharSequence>("base")

class UnchangedText : Slot<String> {
    override fun read(): String = "unchanged"
}

interface PairSlot<A, B> { fun left(): A; fun right(): B }
interface Marker<T>
class MixedText : PairSlot<CharSequence, Int?>, Marker<String> {
    override fun left(): CharSequence = "mixed"
    override fun right(): Int? = null
}
