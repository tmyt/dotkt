package interop.collectionequality

import NUnit.Framework.TestAttribute
import EqualityInterop.*
import MutableListInterop.MutableAndReadOnlyList
import CollectionStorageInterop.MutableSetAndReadOnlyList
import CollectionStorageInterop.AmbiguousCollectionCounts

class CollectionEqualityTests {
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
    }
}
