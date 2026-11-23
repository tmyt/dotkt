package roundtriptests.charsequenceboundaries

import NUnit.Framework.TestAttribute
import roundtrip.upstreamstdlib.BoundarySequence
import roundtrip.upstreamstdlib.SequenceSource
import roundtrip.upstreamstdlib.inlineSequenceIndex
import roundtrip.upstreamstdlib.sequenceIndexInLibrary
import roundtrip.upstreamstdlib.sequenceObjectInLibrary
import roundtrip.upstreamstdlib.sequenceElvisInLibrary

private class StringSource(private val text: String?) : SequenceSource() {
    var reads = 0
    override val source: String? get() { reads++; return text }
}

private open class CollapsedText(val value: CharSequence?) {
    constructor(a: String, b: String) : this(a + b)
}
private class DerivedText(a: String, b: String) : CollapsedText(a + b)

class CharSequenceBoundaryTests {
    @TestAttribute
    fun thisAndSuperDelegationKeepTheSelectedStringSlot() {
        fun rawString(value: Any?) = value is String
        check(rawString(CollapsedText("a", "b").value))
        check(rawString(DerivedText("c", "d").value))
    }
    @TestAttribute
    fun importedReceiverBodiesAndInlineSplicesUseTheSelectedRepresentation() {
        var evaluations = 0
        var callbacks = 0
        fun stringOnce(): String { evaluations++; return "abc" }
        check(inlineSequenceIndex(stringOnce(), { it == 'b' }) { callbacks++; it } == 1)
        check(evaluations == 1 && callbacks == 1)
        check(sequenceIndexInLibrary(stringOnce()) { it == 'b' } == 1)
        check(evaluations == 2)
        val custom = BoundarySequence("abc")
        check(sequenceIndexInLibrary(custom) { it == 'b' } == 1)
        check(custom.length == 3 && custom.toString() == "rendered")
    }

    @TestAttribute
    fun importedConditionalBodiesRetainCustomIdentityAndNullSemantics() {
        val present = StringSource("abc")
        val absent = StringSource(null)
        check(present.first() == 'a' && present.reads == 1)
        check(absent.first() == null && absent.reads == 1)
        val custom = BoundarySequence("abc")
        check(sequenceObjectInLibrary(true, custom).toString() == "raw")
        check(sequenceObjectInLibrary(false, custom) === custom)
        check(sequenceElvisInLibrary(null) == "raw")
        check(sequenceElvisInLibrary(custom) == "rendered")
    }
}
