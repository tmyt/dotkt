package roundtriptests.portablereadonlylist

import NUnit.Framework.TestAttribute
import roundtrip.portablereadonlylist.*

class PortableReadOnlyListTests {
    @TestAttribute fun importedGenericSlicesRetainLiveNullableStorage() {
        val source = arrayListOf<Int?>(0, null, 2, 3)
        val view = slice(source, 1, 3)
        val nested = slice(view, 1, 2)
        check(view == listOf(null, 2) && view.indexOf(null) == 0)
        source[2] = 9
        check(view.last() == 9 && nested.single() == 9)
        check(view.hashCode() == listOf(null, 9).hashCode())
    }

    @TestAttribute fun importedIteratorPreservesPositionAfterBoundaryFailures() {
        val source: List<String> = arrayListOf("first", "last")
        val iterator = cursor(source, 0)
        var before = false
        try { iterator.previous() } catch (_: NoSuchElementException) { before = true }
        check(before && iterator.nextIndex() == 0 && iterator.next() == "first")
        check(iterator.next() == "last")
        var after = false
        try { iterator.next() } catch (_: NoSuchElementException) { after = true }
        check(after && iterator.nextIndex() == 2 && iterator.previous() == "last")
    }
}
