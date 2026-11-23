package roundtriptests.charsequenceboundaries

import NUnit.Framework.TestAttribute
import roundtrip.upstreamstdlib.BoundarySequence
import roundtrip.upstreamstdlib.SequenceSource
import roundtrip.upstreamstdlib.inlineSequenceIndex
import roundtrip.upstreamstdlib.sequenceIndexInLibrary
import roundtrip.upstreamstdlib.sequenceObjectInLibrary
import roundtrip.upstreamstdlib.sequenceElvisInLibrary
import roundtriptests.sequenceresults.LocalSequenceResult

private class StringSource(private val text: String?) : SequenceSource() {
    var reads = 0
    override val source: String? get() { reads++; return text }
}

class CharSequenceBoundaryTests {
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
    fun localGenericMembersReturnCollapsedStrings() {
        val holder = LocalSequenceResult(42, "hello")
        var reads = 0
        fun once(): LocalSequenceResult<Int> { reads++; return holder }
        check(once().source?.length == 5 && reads == 1)
        check(once().read()?.length == 5 && reads == 2)
        check(LocalSequenceResult(1, null).source == null && holder.item == 42)
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
