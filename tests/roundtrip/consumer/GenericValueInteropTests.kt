package roundtriptests.genericvalueinterop

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import GenericValueInterop.NativeBoxApi
import kotlin.clr.byref
import roundtrip.genericvalueinterop.*

class GenericValueInteropTests {
    @TestAttribute
    fun nativeManagedReferencesObserveAliasingDuringTheCall() {
        var box = Box("initial")
        box = Box("before native call")
        val initial = box
        assertTrue(NativeBoxApi.ReplaceAliased(byref(box), byref(box)))
        assertEquals("second", box.value)
        assertEquals("before native call", initial.value)
        assertTrue(box !== initial)
        box = Box("after native call")
        assertEquals("after native call", box.value)

        var number = Box(17)
        val oldNumber = number
        assertTrue(NativeBoxApi.ReplaceAliasedGeneric(byref(number), byref(number), 41, 42))
        assertEquals(42, number.value)
        assertEquals(17, oldNumber.value)
        assertTrue(number !== oldNumber)

        var nested = NativeBoxApi.ReadNested()
        val oldNested = nested
        assertTrue(NativeBoxApi.ReplaceAliasedNested(byref(nested), byref(nested)))
        assertEquals("second", nested.value.value)
        assertTrue(nested !== oldNested)
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
