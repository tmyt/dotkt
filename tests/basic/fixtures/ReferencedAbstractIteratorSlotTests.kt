import NUnit.Framework.TestAttribute

class ReferencedAbstractIteratorSlotTests {
    @TestAttribute fun referencedAbstractReceiverKeepsTheMutableIteratorSlot() {
        val items = mutableListOf("first", "second")
        var calls = 0
        val collection: AbstractMutableCollection<String> = object : AbstractMutableCollection<String>() {
            override val size: Int get() = items.size
            override fun add(element: String): Boolean = items.add(element)
            override fun iterator(): MutableIterator<String> { calls++; return items.iterator() }
        }
        val iterator = collection.iterator()
        check(iterator.hasNext() && iterator.next() == "first")
        iterator.remove()
        check(items.size == 1 && items[0] == "second" && calls == 1)
    }
}
