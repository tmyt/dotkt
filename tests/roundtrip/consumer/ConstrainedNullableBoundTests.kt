// #345 consumer half: the receiver bound and member declaration come from a projected reference KLIB. The bound at
// `Int?` physically closes to ReferencedBoundSink<object>, so the constrained call must box its Int argument into the
// substituted slot. The Any? twin proves an ordinary reference already inhabiting object is left untouched.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert
import roundtrip.constrainedbound.ReferencedAnyBoundSink
import roundtrip.constrainedbound.ReferencedBoundSink
import roundtrip.constrainedbound.ReferencedIntBoundSink

private fun <T : ReferencedBoundSink<Int?>> useReferencedNullableValueBound(t: T): String = t.accept(2)
private fun <T : ReferencedBoundSink<Any?>> useReferencedObjectBound(t: T): String = t.accept("y")

class ConstrainedNullableBoundTests {
    @TestAttribute
    fun constrainedCallUsesReferencedSubstitutedErasedParameterSlot() {
        ClassicAssert.AreEqual("i:2", useReferencedNullableValueBound(ReferencedIntBoundSink()))
        ClassicAssert.AreEqual("a:y", useReferencedObjectBound(ReferencedAnyBoundSink()))
    }
}
