// #345 consumer half: the receiver bound and member declaration come from a projected reference KLIB. The bound at
// `Int?` physically closes to ReferencedBoundSink<object>, so the constrained call must box its Int argument into the
// substituted slot. The Any? twin proves an ordinary reference already inhabiting object is left untouched.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert
import roundtrip.constrainedbound.ReferencedAnyBoundSink
import roundtrip.constrainedbound.ReferencedBoundSink
import roundtrip.constrainedbound.ReferencedIntBoundSink
import roundtrip.constrainedbound.PendingItems
import roundtrip.constrainedbound.ResultSource.ResultItem
import roundtrip.constrainedbound.localItemResult
import roundtrip.constrainedbound.StreamChunk
import roundtrip.constrainedbound.firstChunk
import System.Collections.Generic.List as ClrList

private fun <T : ReferencedBoundSink<Int?>> useReferencedNullableValueBound(t: T): String = t.accept(2)
private fun <T : ReferencedBoundSink<Any?>> useReferencedObjectBound(t: T): String = t.accept("y")

class ConstrainedNullableBoundTests {
    @TestAttribute
    fun constructedAndStarredSubclassesSatisfyTheClassBound() {
        check(localItemResult() == 17L)
        val queue = PendingItems<ResultItem<*>>()
        val numeric = ResultItem(42L)
        val text = ResultItem("text")
        queue.put(numeric)
        queue.put(text)
        check(queue.get(0) === numeric && queue.get(0).result == 42L)
        check(queue.get(1) === text && queue.get(1).result == "text")
        val exact = ClrList<StreamChunk<Long>>()
        val chunk = StreamChunk(42L)
        exact.Add(chunk)
        @Suppress("UNCHECKED_CAST")
        check(firstChunk(exact as ClrList<StreamChunk<*>>) === chunk)
    }

    @TestAttribute
    fun constrainedCallUsesReferencedSubstitutedErasedParameterSlot() {
        ClassicAssert.AreEqual("i:2", useReferencedNullableValueBound(ReferencedIntBoundSink()))
        ClassicAssert.AreEqual("a:y", useReferencedObjectBound(ReferencedAnyBoundSink()))
    }
}
