package roundtriptests.charsequenceboundaries

import NUnit.Framework.TestAttribute
import roundtriptests.builderconstruction.checkBuilderConstructorSnapshot
import roundtrip.portabletext.snapshotBuilder
import roundtrip.portabletext.textVector
import roundtrip.portabletext.echoTextVector
import roundtrip.portabletext.writeTextVector
import roundtrip.upstreamstdlib.BoundarySequence
import roundtrip.upstreamstdlib.LibraryConstructorText
import roundtrip.upstreamstdlib.SequenceSource
import roundtrip.upstreamstdlib.inlineSequenceIndex
import roundtrip.upstreamstdlib.sequenceIndexInLibrary
import roundtrip.upstreamstdlib.sequenceObjectInLibrary
import roundtrip.upstreamstdlib.sequenceElvisInLibrary
import roundtrip.upstreamstdlib.sequenceLengthInLibrary
import roundtrip.upstreamstdlib.LibrarySequenceVector
import roundtriptests.sequenceresults.LocalSequenceResult

private class StringSource(private val text: String?) : SequenceSource() {
    var reads = 0
    override val source: String? get() { reads++; return text }
}

private open class CollapsedText(val value: CharSequence?) {
    constructor(a: String, b: String) : this(a + b)
}
private class DerivedText(a: String, b: String) : CollapsedText(a + b)
private class ImportedText(value: BoundarySequence?) : CollapsedText(value)

class CharSequenceBoundaryTests {
    @TestAttribute
    fun collapsedArrayReturnsPreserveTheOriginalStorage() {
        val builder = StringBuilder("before")
        val original = textVector(builder)
        builder.append("!")
        val returned = echoTextVector(original)
        check(returned === original && returned[0] == "before")
        writeTextVector(returned, builder)
        builder.append("?")
        check(original[0] == "before!")
        returned[0] = "after"
        check(original[0] == "after")
        val foreign = LibrarySequenceVector()
        foreign.elements()[0] = "foreign"
        check(foreign.length() == 7)
    }

    @TestAttribute
    fun importedReceiverBodiesAndInlineSplicesUseTheSelectedRepresentation() {
        checkBuilderConstructorSnapshot()
        val builder = StringBuilder("abc")
        val frozenPresent = snapshotBuilder { builder }
        val frozenAbsent = snapshotBuilder { null }
        fun imported(value: StringBuilder?) = LibraryConstructorText(value)
        val stored = imported(builder)
        builder.append('d')
        check(frozenPresent.value?.length == 3 && frozenAbsent.value == null)
        check(stored.size() == 3 && imported(null).size() == -1)
        fun rawString(value: Any?) = value is String
        check(rawString(CollapsedText("a", "b").value))
        check(rawString(DerivedText("c", "d").value))
        check(rawString(ImportedText(BoundarySequence("abc")).value) && ImportedText(null).value == null)
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
        check(holder.lengthOf(once().read()) == 5 && reads == 3)
        var arguments = 0
        fun argument(): Int { arguments++; return arguments }
        check(sequenceLengthInLibrary(holder.pick(argument())) == 5 && arguments == 1)
        check(sequenceLengthInLibrary(LocalSequenceResult.text(0)) == 5)
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
