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

class InheritedMapSlotTests {
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
