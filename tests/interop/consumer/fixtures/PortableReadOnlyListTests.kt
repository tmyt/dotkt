package interoptests.portablereadonlylist

import NUnit.Framework.TestAttribute
import CollectionStorageInterop.AmbiguousCollectionCounts
import CollectionStorageInterop.RawAndGenericList

class PortableReadOnlyListTests {
    @TestAttribute fun selectedReadonlyFacesSurviveInheritedPolicyAndNestedViews() {
        val native: Any = AmbiguousCollectionCounts()
        val integers = (native as List<Int>).subList(0, 2)
        val strings = (native as List<String>).subList(0, 3)
        check(integers == listOf(0, 1) && integers.indexOf(1) == 1 && integers.lastIndexOf(0) == 0)
        check(strings == listOf("item", "item", "item") && strings.lastIndexOf("item") == 2)
        check(integers.subList(1, 2).single() == 1)
        check(strings.subList(1, 3).subList(1, 2).single() == "item")
        check(strings.listIterator(3).previous() == "item")
    }

    @TestAttribute fun genericReadonlyFaceDoesNotFallBackToRawListStorage() {
        val native: Any = RawAndGenericList()
        val values = (native as List<Int>).subList(0, 2)
        check(values == listOf(7, 8) && values.size == 2)
        check(values.indexOf(7) == 0 && values.lastIndexOf(8) == 1)
        check(values.subList(1, 2).single() == 8)
        check(values.listIterator(2).previous() == 8)
    }
}
