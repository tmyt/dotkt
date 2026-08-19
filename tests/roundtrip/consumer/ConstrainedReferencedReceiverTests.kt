import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert
import RoundtripPropertyInterop.IPropertySlot
import roundtrip.constrainedreceiver.ReferencedReceiverIntLeaf
import roundtrip.constrainedreceiver.ReferencedReceiverLeaf

// #325: a referenced Kotlin interface must stay on the Kotlin call path until constrained receiver binding sees the
// type-variable receiver. Reclassifying it as clrInstance/clrPropGet/clrPropSet first loses the managed-address
// dispatch shape and leaves unverifiable callvirt IL even though reference-type instantiations happen to run.
private fun <T : ReferencedReceiverLeaf<Int>> useReferencedReceiver(t: T): Int {
    val produced = t.produce()
    t.slot = "$produced!"
    return t.slot.length + t.leaf()
}

private class ClrPropertyValue(initial: Int) : IPropertySlot {
    override var value: Int = initial
}

private fun <T : IPropertySlot> useClrPropertyReceiver(t: T): Int {
    val before = t.value
    t.value = before + 7
    return t.value
}

private fun <T : CharSequence> useMappedPropertyReceiver(t: T): Int = t.length

class ConstrainedReferencedReceiverTests {
    @TestAttribute
    fun referencedMethodAndPropertyDispatchThroughTypeParameterReceiver() {
        ClassicAssert.AreEqual(16, useReferencedReceiver(ReferencedReceiverIntLeaf(10)))
        ClassicAssert.AreEqual(18, useClrPropertyReceiver(ClrPropertyValue(11)))
        ClassicAssert.AreEqual(4, useMappedPropertyReceiver("four"))
    }
}
