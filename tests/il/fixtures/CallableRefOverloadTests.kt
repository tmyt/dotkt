// #203 regression — callable references to same-owner overloads must retain the resolved parameter signature through
// kotc -> bir2cir -> ilemit. calleeOwner distinguishes packages, but these pairs deliberately share one file class or
// one declaring class, so a name-only ldftn would bind both delegates to whichever overload was registered first.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

fun crOvlPick(value: Int): String = "top-int:" + value
fun crOvlPick(value: String): String = "top-string:" + value

class CrOvlPicker {
    fun pick(value: Int): String = "bound-int:" + value
    fun pick(value: String): String = "bound-string:" + value
}

class CallableRefOverloadTests {
    @TestAttribute
    fun topLevelBoundAndUnboundReferencesUseResolvedOverload() {
        val topInt: (Int) -> String = ::crOvlPick
        val topString: (String) -> String = ::crOvlPick
        assertEquals("top-int:7", topInt(7))
        assertEquals("top-string:x", topString("x"))

        val picker = CrOvlPicker()
        val boundInt: (Int) -> String = picker::pick
        val boundString: (String) -> String = picker::pick
        assertEquals("bound-int:8", boundInt(8))
        assertEquals("bound-string:y", boundString("y"))

        val unboundInt: (CrOvlPicker, Int) -> String = CrOvlPicker::pick
        val unboundString: (CrOvlPicker, String) -> String = CrOvlPicker::pick
        assertEquals("bound-int:9", unboundInt(picker, 9))
        assertEquals("bound-string:z", unboundString(picker, "z"))
    }
}
