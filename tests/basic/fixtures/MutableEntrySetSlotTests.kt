package mutableentryslots

import NUnit.Framework.TestAttribute

private class Direct<K, V>(val items: MutableSet<MutableMap.MutableEntry<K, V>>) :
    MutableSet<MutableMap.MutableEntry<K, V>> by items {
    override val size get() = items.size
}

private abstract class EntrySetBase<K, V, E>(val map: MutableMap<K, V>) : MutableSet<E> {
    override val size get() = map.size
    override fun clear() = map.clear()
    override fun isEmpty() = map.isEmpty()
}

private class Entries<K, V>(map: MutableMap<K, V>) :
    EntrySetBase<K, V, MutableMap.MutableEntry<K, V>>(map) {
    override fun add(element: MutableMap.MutableEntry<K, V>) = map.entries.add(element)
    override fun addAll(elements: Collection<MutableMap.MutableEntry<K, V>>) = map.entries.addAll(elements)
    override fun iterator() = map.entries.iterator()
    override fun remove(element: MutableMap.MutableEntry<K, V>) = map.entries.remove(element)
    override fun removeAll(elements: Collection<MutableMap.MutableEntry<K, V>>) = map.entries.removeAll(elements)
    override fun retainAll(elements: Collection<MutableMap.MutableEntry<K, V>>) = map.entries.retainAll(elements)
    override fun contains(element: MutableMap.MutableEntry<K, V>) = map.entries.contains(element)
    override fun containsAll(elements: Collection<MutableMap.MutableEntry<K, V>>) = map.entries.containsAll(elements)
}

class MutableEntrySetSlotTests {
    @TestAttribute
    fun entrySetsImplementTheirClosedSlotsAndWriteThroughToTheOriginalMap() {
        val map = mutableMapOf("key" to 1)
        fun verify(entries: MutableSet<MutableMap.MutableEntry<String, Int>>) {
            check(entries.size == 1)
            val entry = entries.iterator().next()
            check(entry.setValue(7) == 1 && map["key"] == 7)
            check(entries.remove(entry) && map.isEmpty())
        }
        verify(Direct(map.entries))
        map["key"] = 1
        val base: EntrySetBase<String, Int, MutableMap.MutableEntry<String, Int>> = Entries(map)
        verify(base)
    }
}
