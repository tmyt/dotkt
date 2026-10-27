import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.defaultpropertyslot.ReferencedCompareMarker
import roundtrip.defaultpropertyslot.ReferencedGenericRenamedDefaultMethodSlot
import GenericValueInterop.NativeTextComparisonBase

private open class ImportedGenericDefaultCollisionBase<T> {
    fun CompareTo(other: ReferencedGenericRenamedDefaultMethodSlot<List<T>>): Int = 363
    fun CompareTo(other: ReferencedCompareMarker<List<T>>): Int = 365
}

private class ImportedGenericDefaultCollision<T> : ImportedGenericDefaultCollisionBase<T>(),
    ReferencedGenericRenamedDefaultMethodSlot<List<T>>

private interface NativeDefaultListSource<T> : Comparable<NativeDefaultListSource<T>> {
    override fun compareTo(other: NativeDefaultListSource<T>): Int = 366
}

private class NativeDefaultListImplementation<T> : NativeTextComparisonBase(), NativeDefaultListSource<List<T>>

class InheritedGenericDefaultCollisionTests {
    @TestAttribute
    fun referencedGenericDefaultRetainsEverySelectedNativeSlot() {
        val instance = ImportedGenericDefaultCollision<String>()
        val selected: ReferencedGenericRenamedDefaultMethodSlot<List<String>> = instance
        val native: Comparable<ReferencedGenericRenamedDefaultMethodSlot<List<String>>> = instance
        assertEquals(362, selected.compareTo(instance))
        assertEquals(362, native.compareTo(instance))
        assertEquals(363, instance.CompareTo(instance))

        val marker = ReferencedCompareMarker(listOf("value"))
        assertEquals(364, selected.compareTo(marker))
        assertEquals(365, instance.CompareTo(marker))

        val multiple = NativeDefaultListImplementation<String>()
        val direct: NativeDefaultListSource<List<String>> = multiple
        assertEquals(366, direct.compareTo(multiple))
        val nativeView: Comparable<NativeDefaultListSource<List<String>>> = multiple
        assertEquals(366, nativeView.compareTo(multiple))
        assertEquals(77, multiple.CompareTo("native"))
    }
}
