package roundtriptests.iteratorprotocol

import NUnit.Framework.TestAttribute
import roundtrip.iteratorprotocol.*

private class GenericIteratorMethod {
    var calls = 0
    fun <T> iterator(): Iterator<T> { calls++; return emptyList<T>().iterator() }
}

private class GenericMutableIteratorMethod {
    var calls = 0
    fun <T> iterator(): MutableIterator<T> { calls++; return mutableListOf<T>().listIterator() }
}

private fun <T> checkInheritedIterator(first: T, second: T) {
    val values = mutableListOf(first, second)
    val set = inheritedIteratorSet(values)
    val iterator = set.iterator()
    check(iterator.hasNext() && iterator.next() == first)
    iterator.remove()
    check(values.size == 1 && values[0] == second && set.contains(second))
    check(set.remove(second) && values.isEmpty())
    set.add(first)
    set.clear()
    check(values.isEmpty())

    val otherValues = mutableListOf(first, second)
    val other = otherIteratorOwner(otherValues)
    val otherIterator = other.iterator()
    check(otherIterator.next() == first)
    otherIterator.remove()
    check(otherValues.size == 1 && otherValues[0] == second)
    val otherCollection: MutableCollection<T> = other
    otherCollection.clear()
    check(otherValues.isEmpty())
}

class InheritedIteratorProtocolTests {
    @TestAttribute fun abstractMutableSetCallsKeepTheMutableIteratorContract() {
        val items = mutableSetOf("first", "second")
        val collection: AbstractMutableSet<String> = object : AbstractMutableSet<String>() {
            override val size: Int get() = items.size
            override fun add(element: String): Boolean = items.add(element)
            override fun iterator(): MutableIterator<String> = items.iterator()
        }
        val iterator = collection.iterator()
        check(iterator.hasNext())
        iterator.next()
        iterator.remove()
        check(items.size == 1)
        collection.clear()
        check(items.isEmpty())
        val generic = GenericIteratorMethod()
        check(!generic.iterator<String>().hasNext() && generic.calls == 1)
        val genericMutable = GenericMutableIteratorMethod()
        check(!genericMutable.iterator<Int>().hasNext() && genericMutable.calls == 1)
    }

    @TestAttribute fun referencedOwnersUseTheIteratorElementAndInheritedMutationBodies() {
        checkInheritedIterator("first", "second")
        checkInheritedIterator(7, 9)
        checkInheritedIterator<Int?>(null, 5)
    }
}
