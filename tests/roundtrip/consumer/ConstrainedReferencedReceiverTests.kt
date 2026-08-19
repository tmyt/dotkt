import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert
import roundtrip.constrainedreceiver.ReferencedReceiverIntLeaf
import roundtrip.constrainedreceiver.ReferencedReceiverLeaf

// #325: a referenced Kotlin interface must stay on the Kotlin call path until constrained receiver binding sees the
// type-variable receiver. Reclassifying it as clrInstance/clrPropGet/clrPropSet first loses the managed-address
// dispatch shape and leaves unverifiable callvirt IL even though reference-type instantiations happen to run.
private fun <T : ReferencedReceiverLeaf<Int>> useReferencedReceiver(t: T): Int {
    val produced = t.produce()
    t.slot = produced + 1
    return t.slot + t.leaf()
}

class ConstrainedReferencedReceiverTests {
    @TestAttribute
    fun referencedMethodAndPropertyDispatchThroughTypeParameterReceiver() {
        ClassicAssert.AreEqual(16, useReferencedReceiver(ReferencedReceiverIntLeaf(10)))
    }
}
