package inheritedmapslots

import NUnit.Framework.TestAttribute

class Key<T>(val make: () -> T)
interface Value<T> { fun read(): T }
class Cell<T>(val item: T) : Value<T> { override fun read(): T = item }
class Entry<K, V>(val key: K, val value: V)

interface ReadMap<K, out V> : Map<K, V>
interface EditMap<K, out V> : ReadMap<K, V> {
    fun entry(): Entry<K, @UnsafeVariance V>
}
open class Backed<K, V>(private val backing: Map<K, V>) : AbstractMap<K, V>(), EditMap<K, V> {
    override val entries: Set<Map.Entry<K, V>> get() = backing.entries
    override fun entry(): Entry<K, V> = entries.first().let { Entry(it.key, it.value) }
    override fun containsKey(key: K): Boolean = backing.containsKey(key)
    override fun isEmpty(): Boolean = backing.isEmpty()
}
interface Specialized : EditMap<Key<Any?>, Value<Any?>>
class Inherited(m: Map<Key<Any?>, Value<Any?>>) : Backed<Key<Any?>, Value<Any?>>(m), Specialized
class Redeclared(m: Map<Key<Any?>, Value<Any?>>) : Backed<Key<Any?>, Value<Any?>>(m), Specialized {
    override fun containsKey(key: Key<Any?>): Boolean = super.containsKey(key)
}

private interface EntryView<out E> : Set<E>
private class MapEntryView<E>(val source: Set<E>) : AbstractSet<E>(), EntryView<E> {
    override val size get() = source.size
    override fun iterator() = source.iterator()
}
private class NarrowMap<K, V>(source: Map<K, V>) : AbstractMap<K, V>() {
    override val entries: EntryView<Map.Entry<K, V>> = MapEntryView(source.entries)
}
private fun <K, V> baseEntries(map: AbstractMap<K, V>): Set<Map.Entry<K, V>> = map.entries

class InheritedMapSlotTests {
    @TestAttribute fun inheritedAbstractGetterDispatchesToCovariantOverride() {
        val map = NarrowMap(mapOf("key" to 7))
        check(baseEntries(map) === map.entries)
        check(map.entries.size == 1)
    }

    @TestAttribute fun dispatchesInheritedAndRedeclaredSlotsThroughClosedInterface() {
        val key = Key<Any?> { "key" }
        val value: Value<Any?> = Cell<Any?>(7)
        val entries = mapOf(key to value)
        fun verify(map: Specialized) {
            check(!map.isEmpty() && map.containsKey(key))
            check(map.containsValue(value) && map[key] === value)
        }
        verify(Inherited(entries))
        verify(Redeclared(entries))
    }
}
