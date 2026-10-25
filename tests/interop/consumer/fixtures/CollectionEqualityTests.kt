package interop.collectionequality

import NUnit.Framework.TestAttribute
import EqualityInterop.*
import MutableListInterop.MutableAndReadOnlyList
import MutableListInterop.SameElementList
import CollectionStorageInterop.MutableSetAndReadOnlyList
import CollectionStorageInterop.AmbiguousCollectionCounts

class CollectionEqualityTests {
    @TestAttribute
    fun nullableForeignClassifierGuardsDoNotCallEquals() {
        val value = OverriddenList()
        val opaque: Any? = value
        check(opaque is System.Collections.Generic.List<*>?)
        check((opaque as System.Collections.Generic.List<*>?) === value)
        check(value.Calls == 0)
        val absent: Any? = null
        check(absent is System.Collections.Generic.List<*>?)
        check((absent as System.Collections.Generic.List<*>?) === null)
    }

    @TestAttribute
    fun realStdlibDefaultsPreserveTheNativeEqualsHashContract() {
        val first = LinkedHashSet<Int>()
        first.add(23); first.add(42)
        val second = LinkedHashSet<Int>()
        second.add(42); second.add(23)
        check(NativeEqualityContract.EqualHashAndLookup(first, second))
        val nestedFirst = LinkedHashSet<Any>()
        nestedFirst.add(listOf(23))
        nestedFirst.add(mapOf(1 to "x", 2 to "y"))
        val nestedSecond = LinkedHashSet<Any>()
        nestedSecond.add(listOf(23))
        nestedSecond.add(mapOf(2 to "y", 1 to "x"))
        check(NativeEqualityContract.EqualHashAndLookup(nestedFirst, nestedSecond))
        val left: Any = mutableListOf<Any?>(listOf(23), "x", null).subList(0, 3)
        val right: Any = mutableListOf<Any?>(listOf(23), "x", null).subList(0, 3)
        check(NativeEqualityContract.EqualHashAndLookup(left, right))
    }

    @TestAttribute
    fun mapEqualityUsesLookupRatherThanACartesianEntryScan() {
        var smallerCalls = 0
        for (size in arrayOf(32, 64)) {
            val left = HashMap<LookupKey, Int>()
            val right = HashMap<LookupKey, Int>()
            for (index in 0 until size) { left[LookupKey(index)] = index; right[LookupKey(index)] = index }
            LookupKey.Calls = 0
            check(left == right)
            val calls = LookupKey.Calls
            check(calls > 0)
            // Doubling the input doubles lookup work, while a Cartesian entry scan grows approximately fourfold.
            // Allow constant-factor storage differences without assuming a fixed number of queries per key.
            if (smallerCalls != 0) check(calls <= smallerCalls * 3)
            smallerCalls = calls
        }
    }

    @TestAttribute
    fun nativeOverridesWinButNewSlotHidingDoesNotReplaceObjectEquals() {
        val overridden = OverriddenList()
        val opaque: Any = overridden
        check(!opaque.equals(opaque) && overridden.Calls == 1)
        check(!(opaque == opaque) && overridden.Calls == 2)
        check(!opaque.equals(null) && overridden.Calls == 3)
        val hidden: Any = HiddenList()
        check(hidden == listOf(7, 9))
    }

    @TestAttribute
    fun boundClassFamilyIgnoresUnrelatedForeignFaces() {
        val first: Any = MutableAndReadOnlyList()
        val second: Any = MutableAndReadOnlyList()
        val ordinary: Any = listOf(7, 9)
        check(first == ordinary && ordinary == first && first == second)
        val set: Any = MutableSetAndReadOnlyList()
        val ordinarySet: Any = setOf("a", "b", "c")
        check(set == ordinarySet && ordinarySet == set)
        check(set != listOf(0, 1))
        val ambiguous: Any = AmbiguousCollectionCounts()
        check(!ordinary.equals(ambiguous))
        // The same exact native readonly interface can be reimplemented. Its
        // Count stays virtual; equality does not read private List storage.
        val reimplemented: Any = SameElementList()
        check((reimplemented as List<*>).size == 3)
        check((reimplemented as MutableList<*>).size == 2)
        check(!reimplemented.equals(ordinary))
    }
}
