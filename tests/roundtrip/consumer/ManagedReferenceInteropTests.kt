package roundtriptests.genericvalueinterop

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import GenericValueInterop.NativeBoxApi
import GenericValueInterop.NativeSlot
import System.InvalidCastException
import kotlin.clr.byref
import roundtrip.genericvalueinterop.*

private fun <T> readNativeSlot(slot: NativeSlot<T>): T {
    var value by byref(slot.Reference())
    return value
}

class ManagedReferenceInteropTests {
    @TestAttribute
    fun genericLiveReferenceLoadsItsActualReferent() {
        val number = NativeSlot<Int>(17)
        assertEquals(17, readNativeSlot(number))
        number.Replace(42)
        assertEquals(42, readNativeSlot(number))
        val text = NativeSlot<String>("initial")
        assertEquals("initial", readNativeSlot(text))
        text.Replace("updated")
        assertEquals("updated", readNativeSlot(text))
    }

    @TestAttribute
    fun nominalAliasValuesEnterExactReferenceStorage() {
        val replacement = NativeBoxApi.CreateNames("updated")
        NativeBoxApi.ReplaceNamesThroughKotlin(replacement)
        assertTrue(NativeBoxApi.ReadNames() === replacement)
        assertEquals("updated", NativeBoxApi.ReadFirstName())
    }

    @TestAttribute
    fun carrierArraysEnterExactReferenceStorage() {
        var boxes by byref(NativeBoxApi.BoxesReference())
        val replacement: Array<Box<String>> = NativeBoxApi.CreateBoxes("updated")
        replaceBoxesRef(byref(boxes), replacement)
        assertTrue(NativeBoxApi.ReadBoxes() === replacement)
        assertEquals("updated", boxes[0].value)
    }

    @TestAttribute
    fun managedReferenceDeclarationsKeepNativeStorageAcrossAssemblies() {
        NativeBoxApi.ResetText("initial")
        var text by byref(NativeBoxApi.TextReference())
        val original = text
        text = Box("direct")
        assertTrue(NativeBoxApi.ReadText() === text)
        assertEquals("initial", original.value)
        replaceTextBoxRef(byref(text), Box("parameter"))
        assertEquals("parameter", text.value)
        replaceBoxRef(byref(text), Box("generic"))
        assertEquals("generic", text.value)
        var echoed by byref(NativeBoxApi.EchoReference(byref(text)))
        assertTrue(NativeBoxApi.ReplaceAliased(byref(text), byref(echoed)))
        assertTrue(text === echoed && NativeBoxApi.ReadText() === text)
        assertEquals("second", text.value)
        NativeBoxApi.ResetText("external")
        assertEquals("external", text.value)
        assertEquals("external", echoed.value)
        var number by byref(NativeBoxApi.NumberReference())
        replaceBoxRef(byref(number), Box(42))
        assertEquals(42, number.value)
        assertTrue(NativeBoxApi.ReadNumber() === number)
        var nested by byref(NativeBoxApi.NestedReference())
        replaceBoxRef(byref(nested), Box(Box("nested updated")))
        assertTrue(NativeBoxApi.ReadNested() === nested)
        assertEquals("nested updated", nested.value.value)
        val raw: Any = Box(99)
        @Suppress("UNCHECKED_CAST")
        val unchecked = raw as Box<String>
        assertTrue(unchecked === raw)
        var rejected = false
        try {
            text = unchecked
        } catch (e: InvalidCastException) {
            rejected = true
        }
        assertTrue(rejected)
        assertEquals("external", text.value)
        rejected = false
        try {
            replaceTextBoxRef(byref(text), unchecked)
        } catch (e: InvalidCastException) {
            rejected = true
        }
        assertTrue(rejected)
        assertTrue(NativeBoxApi.ReadText() === text)
        assertEquals("external", text.value)
    }
}
