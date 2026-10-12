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
        ClassicAssert.AreEqual(45, owner.privateNestedValue())
        ClassicAssert.AreEqual(46, roundtrip.staticnested.StaticNestedDerived().protectedNestedValue())
        ClassicAssert.AreEqual(47, ProtectedNestedConsumer().read())
        ClassicAssert.AreEqual(48, StaticNestedOwner.WithCompanion.value())
        val leaf: StaticNestedOwner.Box.Leaf = roundtrip.staticnested.makeStaticLeaf()
        ClassicAssert.IsNotNull(leaf)
        ClassicAssert.IsTrue(roundtrip.staticnested.echoStaticEntry(entry) === entry)
        val relayed: StaticNestedOwner.Closed = mpp.app.staticNestedField
        ClassicAssert.AreEqual(42, relayed.marker())
        ClassicAssert.AreEqual(49, DerivedFailed().marker())
    }
}

private class DerivedFailed : StaticNestedOwner.Failed() {
    override fun marker(): Int = 49
}

private class ProtectedNestedConsumer : StaticNestedOwner<OwnerValue>(OwnerValue()) {
    fun read(): Int = ProtectedValue(47).value
}
