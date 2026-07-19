// hashCode / CLR-native-GetHashCode battery — migrates cases/il-strhash (#167/#168). The old case
// println'd booleans as "True"/"False" and diffed the text; asserting the boolean via IsTrue is stronger
// (a wrong non-boolean can't accidentally stringify to "True"). Asserts CONTRACT (equals-consistency +
// hash-set membership), never a pinned hash integer — the design doctrine (JVM is not a compat target).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert

class HashCodeTests {
    @TestAttribute
    fun stringHashContract() {
        ClassicAssert.IsTrue("Aa".hashCode() == "Aa".hashCode())
        ClassicAssert.IsTrue("hello".hashCode() == ("hel" + "lo").hashCode())
        ClassicAssert.IsTrue(hashSetOf("a", "b", "c").contains("b"))
    }

    @TestAttribute
    fun floatDoubleHashContract() {
        ClassicAssert.IsTrue(Double.NaN.hashCode() == Double.NaN.hashCode())
        ClassicAssert.IsTrue(hashSetOf(1.5, 2.5).contains(1.5))
        ClassicAssert.IsTrue((-0.0f).hashCode() == (-0.0f).hashCode())
    }

    @TestAttribute
    fun primitiveIntStaysOnBclSlot() {
        ClassicAssert.AreEqual("5", 5.toString())
        ClassicAssert.IsTrue(5.equals(5))
        ClassicAssert.AreEqual(5, 5.hashCode())       // Int32.GetHashCode returns the value itself
        ClassicAssert.AreEqual(-7, (-7).hashCode())
        ClassicAssert.AreEqual("2a", 42.toString(16))
    }
}
