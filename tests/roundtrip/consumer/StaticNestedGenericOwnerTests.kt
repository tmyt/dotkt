import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert
import roundtrip.staticnested.StaticNestedOwner
import roundtrip.staticnested.OwnerValue
import roundtrip.staticnested.makeStaticClosed
import roundtrip.staticnested.makeStaticBox
import roundtrip.staticnested.staticPrivateAccess

class StaticNestedGenericOwnerTests {
    @TestAttribute
    fun nonInnerClassifiersDoNotCaptureGenericOwners() {
        val failed: StaticNestedOwner.Failed = makeStaticClosed()
        ClassicAssert.AreEqual(42, failed.marker())
        ClassicAssert.AreEqual(42, StaticNestedOwner.Closed().marker())
        val box: StaticNestedOwner.Box<String> = makeStaticBox("outer")
        val entry: StaticNestedOwner.Box<String>.Entry<Int> = box.Entry(44)
        ClassicAssert.AreEqual("outer", entry.outer())
        ClassicAssert.AreEqual(44, entry.item)
        ClassicAssert.AreEqual(43, StaticNestedOwner.Singleton.marker())
        ClassicAssert.AreEqual(StaticNestedOwner.Choice.ONLY, StaticNestedOwner.Choice.valueOf("ONLY"))
        ClassicAssert.IsTrue(staticPrivateAccess())
        val value = OwnerValue()
        val owner = StaticNestedOwner(value)
        ClassicAssert.IsTrue(owner.Captured().outer() === value)
    }
}
