package roundtriptests.portablesetiterator

import NUnit.Framework.TestAttribute
import roundtrip.portablesetiterator.*

class PortableSetIteratorTests {
    @TestAttribute fun importedLinkedIteratorRemovesFromTheOriginalSetInOrder() {
        val set = linkedSetOf("first", "second", "third")
        val iterator = linkedIterator(set)
        check(iterator.next() == "first")
        check(iterator.hasNext() && iterator.hasNext())
        iterator.remove()
        check(set.toList() == listOf("second", "third"))
        check(iterator.next() == "second")
        iterator.remove()
        check(iterator.next() == "third")
        check(!iterator.hasNext())
        iterator.remove()
        check(set.isEmpty())
    }

    @TestAttribute fun importedNativeIteratorKeepsNullableElementsAndRemoval() {
        val set = HashSet<Int?>()
        set.addAll(listOf(1, null, 2))
        val iterator = nativeIterator(set)
        val values = mutableSetOf<Int?>()
        while (iterator.hasNext()) {
            check(iterator.hasNext())
            check(values.add(iterator.next()))
            iterator.hasNext()
            iterator.remove()
        }
        check(set.isEmpty() && values == setOf(1, null, 2))
    }
}
