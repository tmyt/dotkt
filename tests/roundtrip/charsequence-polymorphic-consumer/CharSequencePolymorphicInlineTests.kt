package roundtriptests.charsequencepolymorphic

import NUnit.Framework.TestAttribute
import roundtrip.upstreamstdlib.inlineSequenceIndex

private class ConsumerSequence(private val text: String) : CharSequence {
    override val length: Int get() = text.length
    override fun get(index: Int): Char = text[index]
    override fun subSequence(startIndex: Int, endIndex: Int): CharSequence =
        ConsumerSequence(text.substring(startIndex, endIndex))
    override fun toString(): String = "consumer-rendered"
}

class CharSequencePolymorphicInlineTests {
    @TestAttribute
    fun importedInlineReceiversAdaptStringsAndRetainCustomSequences() {
        var evaluations = 0
        var callbacks = 0
        var predicates = 0
        fun stringOnce(): String { evaluations++; return "abc" }
        val custom = ConsumerSequence("abc")
        fun customOnce(): CharSequence { evaluations++; return custom }

        check(inlineSequenceIndex(stringOnce(), { predicates++; it == 'b' }) { callbacks++; it } == 1)
        check(evaluations == 1 && callbacks == 1 && predicates == 2)
        check(inlineSequenceIndex(customOnce(), { predicates++; it == 'b' }) { callbacks++; it } == 1)
        check(evaluations == 2 && callbacks == 2 && predicates == 4)
        check((customOnce() as Any) === custom)
        check(evaluations == 3)
        check(custom.length == 3 && custom.toString() == "consumer-rendered")
    }
}
