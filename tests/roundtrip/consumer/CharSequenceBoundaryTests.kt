package roundtriptests.charsequenceboundaries

import NUnit.Framework.TestAttribute
import roundtrip.upstreamstdlib.BoundarySequence
import roundtrip.upstreamstdlib.inlineSequenceIndex
import roundtrip.upstreamstdlib.sequenceIndexInLibrary
import roundtrip.upstreamstdlib.sequenceObjectInLibrary
import roundtrip.upstreamstdlib.sequenceElvisInLibrary

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
    fun importedConditionalBodiesRetainCustomIdentityAndNullSemantics() {
        val custom = BoundarySequence("abc")
        check(sequenceObjectInLibrary(true, custom).toString() == "raw")
        check(sequenceObjectInLibrary(false, custom) === custom)
        check(sequenceElvisInLibrary(null) == "raw")
        check(sequenceElvisInLibrary(custom) == "rendered")
    }
}
