package portablesetiterators

import NUnit.Framework.TestAttribute

private class Token(val id: Int)

private fun <T> invalidRemove(iterator: MutableIterator<T>) {
    var failed = false
    try { iterator.remove() } catch (_: IllegalStateException) { failed = true }
    check(failed)
}

private fun <T> exhausted(iterator: MutableIterator<T>) {
    check(!iterator.hasNext() && !iterator.hasNext())
    var failed = false
    try { iterator.next() } catch (_: NoSuchElementException) { failed = true }
    check(failed)
}

class PortableSetIteratorTests {
    @TestAttribute fun linkedViewsKeepTheBackingIterator() {
        val set = linkedSetOf("first", "second", "third")
        val mutable: MutableSet<String> = set
        val readonly: Set<String> = set
        val iterators = listOf<Iterator<String>>(set.iterator(), mutable.iterator(), readonly.iterator())
        for (iterator in iterators) check(iterator.next() == "first")
        set.add("extra")
        for (iterator in iterators) {
            var failed = false
            try { iterator.hasNext(); iterator.next() }
            catch (_: ConcurrentModificationException) { failed = true }
            check(failed)
        }
    }

    @TestAttribute fun linkedRemovalPreservesNullableIdentityAndCursor() {
        val first = Token(1)
        val last = Token(2)
        val set = linkedSetOf<Token?>(first, null, last)
        val iterator = set.iterator()
        invalidRemove(iterator)
        check(iterator.next() === first)
        check(iterator.hasNext() && iterator.hasNext())
        iterator.remove()
        invalidRemove(iterator)
        check(iterator.next() == null)
        check(iterator.hasNext())
        iterator.remove()
        check(iterator.next() === last)
        exhausted(iterator)
        iterator.remove()
        invalidRemove(iterator)
        check(set.isEmpty())
    }

    @TestAttribute fun linkedBulkOperationsRemoveThroughTheirOwnIterator() {
        val set = linkedSetOf("first", "second", "third")
        check(!set.addAll(set))
        check(!set.retainAll(set))
        check(set.removeAll(set) && set.isEmpty())
        check(!set.removeAll(set) && !set.retainAll(set))
        set.addAll(listOf("first", "second", "third", "fourth"))
        check(set.removeAll(listOf("second", "missing", "first", "first")))
        check(set.toList() == listOf("third", "fourth"))
        check(set.retainAll(listOf("fourth")))
        check(set.toList() == listOf("fourth"))
        check(!set.retainAll(listOf("fourth")))
    }

    @TestAttribute fun linkedRemovalAndReinsertionKeepInsertionOrder() {
        val set = linkedSetOf("first", "second", "third")
        check(set.remove("second") && set.add("second"))
        check(set.toList() == listOf("first", "third", "second"))
        val iterator = set.iterator()
        check(iterator.next() == "first")
        iterator.remove()
        set.add("first")
        check(set.toList() == listOf("third", "second", "first"))
    }

    @TestAttribute fun nativeConcreteRemovalSurvivesBufferedHasNextAndExhaustion() {
        val set = HashSet<Int?>()
        set.addAll(listOf(1, null, 2))
        val seen = mutableSetOf<Int?>()
        val iterator = set.iterator()
        invalidRemove(iterator)
        while (iterator.hasNext()) {
            check(iterator.hasNext())
            check(seen.add(iterator.next()))
            iterator.hasNext()
            iterator.remove()
            invalidRemove(iterator)
        }
        check(set.isEmpty() && seen == setOf(1, null, 2))
        exhausted(iterator)
    }

    @TestAttribute fun nativeConcreteIteratorRetainsTheLiveNativeCursor() {
        val set = HashSet<String>()
        set.addAll(listOf("first", "second", "third"))
        val iterator = set.iterator()
        check(set.contains(iterator.next()))
        set.add("extra")
        var failed = false
        try { iterator.hasNext(); iterator.next() }
        catch (_: System.InvalidOperationException) { failed = true }
        check(failed)
    }
}
