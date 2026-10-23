package roundtriptests.genericvalueinterop

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import GenericValueInterop.NativeBoxApi
import kotlin.clr.byref
import roundtrip.genericvalueinterop.*

private fun replaceAliasedBoxParameter(box: Box<String>): Boolean {
    val initial = box
    return NativeBoxApi.ReplaceAliased(byref(box), byref(box)) &&
        box.value == "second" && initial.value == "initial" && box !== initial
}

class GenericValueInteropTests {
    @TestAttribute
    fun nativeManagedReferencesObserveAliasingDuringTheCall() {
        var box = Box("initial")
        val initial = box
        assertTrue(NativeBoxApi.ReplaceAliased(byref(box), byref(box)))
        assertEquals("second", box.value)
        assertEquals("initial", initial.value)
        assertTrue(box !== initial)

        var fromFactory = createBox()
        val factoryInitial = fromFactory
        assertTrue(NativeBoxApi.ReplaceAliased(byref(fromFactory), byref(fromFactory)))
        assertEquals("second", fromFactory.value)
        assertEquals("initial", factoryInitial.value)
        assertTrue(fromFactory !== factoryInitial)
        assertTrue(replaceAliasedBoxParameter(createBox()))
    }

    @TestAttribute
    fun genericClrFieldsKeepTheirDeclaredOwnerAndStorage() {
        val strings = Cell("initial")
        val alias = strings
        alias.value = "changed"
        assertEquals("changed", strings.value)
        val integers = Cell(17)
        integers.value = 42
        assertEquals(42, integers.value)
    }

    @TestAttribute
    fun constructorsDistinguishInvariantGenericArguments() {
        assertEquals("string", Selected(Box("value")).tag)
        assertEquals("int", Selected(Box(42)).tag)
    }

    @TestAttribute
    fun genericValuesUpcastToTheirBaseWithoutChangingIdentity() {
        val derived = Derived<Int>()
        val base: Base = derived
        assertTrue(base === derived)
    }
}
