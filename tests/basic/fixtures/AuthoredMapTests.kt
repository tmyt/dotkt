import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue

private fun authoredMapQuery(source: Any, key: Any?): Any? = (source as Map<*, *>)[key]

open class AuthoredPlainMapOperations<K, V>(protected val data: MutableMap<K, V>) {
    open val size: Int get() = data.size
    open val keys: MutableSet<K> get() = data.keys
    open val values: MutableCollection<V> get() = data.values
    open val entries: MutableSet<MutableMap.MutableEntry<K, V>> get() = data.entries
    open fun isEmpty(): Boolean = data.isEmpty()
    open fun containsKey(key: K): Boolean = data.containsKey(key)
    open fun containsValue(value: V): Boolean = data.containsValue(value)
    open fun get(key: K): V? = data[key]
    open fun put(key: K, value: V): V? = data.put(key, value)
    open fun remove(key: K): V? = data.remove(key)
    open fun putAll(from: Map<out K, V>) = data.putAll(from)
    open fun clear() = data.clear()
}

private class AuthoredPlainInheritedMap<K, V>(data: MutableMap<K, V>) :
    AuthoredPlainMapOperations<K, V>(data), MutableMap<K, V> {
    override fun get(key: K): V? = super.get(key)
}

open class AuthoredReadOnlyMap<K, V>(protected val backing: MutableMap<K, V>) : Map<K, V> {
    var getCalls = 0
    var containsCalls = 0
    var emptyCalls = 0
    var defaultCalls = 0
    override val size: Int get() = backing.size
    override val keys: Set<K> = backing.keys
    override val values: Collection<V> = backing.values
    override val entries: Set<Map.Entry<K, V>> get() = backing.entries
    override fun isEmpty(): Boolean { emptyCalls++; return backing.isEmpty() }
    override fun containsKey(key: K): Boolean { containsCalls++; return backing.containsKey(key) }
    override fun containsValue(value: V): Boolean = backing.containsValue(value)
    override fun get(key: K): V? { getCalls++; return backing[key] }
    override fun getOrDefault(key: K, defaultValue: V): V {
        defaultCalls++
        return backing.getOrDefault(key, defaultValue)
    }
}

class AuthoredMutableMap<K, V>(backing: MutableMap<K, V>) : AuthoredReadOnlyMap<K, V>(backing), MutableMap<K, V> {
    var putCalls = 0
    var optionalCalls = 0
    override val keys: MutableSet<K> = backing.keys
    override val values: MutableCollection<V> = backing.values
    override val entries: MutableSet<MutableMap.MutableEntry<K, V>> get() = backing.entries
    override fun put(key: K, value: V): V? { putCalls++; return backing.put(key, value) }
    override fun remove(key: K): V? = backing.remove(key)
    override fun putAll(from: Map<out K, V>) = backing.putAll(from)
    override fun clear() = backing.clear()
    override fun putIfAbsent(key: K, value: V): V? { optionalCalls++; return backing.putIfAbsent(key, value) }
    override fun replace(key: K, value: V): V? { optionalCalls++; return backing.replace(key, value) }
    override fun replace(key: K, oldValue: V, newValue: V): Boolean {
        optionalCalls++
        return backing.replace(key, oldValue, newValue)
    }
    override fun remove(key: K, value: V): Boolean { optionalCalls++; return backing.remove(key, value) }
    override fun merge(key: K, value: V, remappingFunction: (V, V) -> V?): V? {
        optionalCalls++
        return backing.merge(key, value, remappingFunction)
    }
}

class AuthoredMapTests {
    @TestAttribute
    fun emptyReadOnlyImplementationLoads() {
        val map: Map<String, String> = AuthoredReadOnlyMap(mutableMapOf())
        assertTrue(map.size == 0 && map.isEmpty())
        assertTrue(map["missing"] == null && !map.containsKey("missing"))
        assertTrue(map.keys.isEmpty() && map.values.isEmpty() && map.entries.isEmpty())
        val inherited: MutableMap<Int, String> = AuthoredPlainInheritedMap(mutableMapOf(1 to "one"))
        assertTrue(inherited.size == 1 && inherited[1] == "one")
        assertTrue(inherited.put(2, "two") == null && inherited.containsKey(2))
        inherited.clear()
        assertTrue(inherited.isEmpty())
    }

    @TestAttribute
    fun interfaceCallsDispatchToAuthoredMembers() {
        val source = AuthoredReadOnlyMap(mutableMapOf(1 to "one"))
        val map: Map<Int, String> = source
        assertTrue(map[1] == "one" && source.getCalls == 1)
        assertTrue(map.containsKey(1) && source.containsCalls == 1)
        assertTrue(!map.isEmpty() && source.emptyCalls == 1)
        assertTrue(map.containsValue("one"))
        assertTrue(map.getOrDefault(2, "default") == "default" && source.defaultCalls == 1)
        assertTrue(map.keys === source.keys && map.values === source.values)
    }

    @TestAttribute
    fun inheritedMutableImplementationPreservesViewsAndPreviousValues() {
        val source = AuthoredMutableMap(mutableMapOf(1 to "one"))
        val map: MutableMap<Int, String> = source
        val keys = map.keys
        assertTrue(map.put(1, "updated") == "one" && source.putCalls == 1)
        assertTrue(map.put(2, "two") == null && source.putCalls == 2)
        assertTrue(keys === source.keys && keys.contains(2))
        assertTrue(map[1] == "updated" && source.getCalls == 1)
        assertTrue(map.remove(1) == "updated" && map.remove(1) == null)
        val entry = map.entries.first { it.key == 2 }
        assertTrue(entry.setValue("two updated") == "two" && map[2] == "two updated")
        assertTrue(map.putIfAbsent(3, "three") == null)
        assertTrue(map.replace(3, "changed") == "three")
        assertTrue(map.replace(3, "changed", "final"))
        assertTrue(map.remove(3, "final"))
        assertTrue(map.merge(4, "four") { a, b -> a + b } == "four")
        assertTrue(map.merge(4, "!") { a, b -> a + b } == "four!")
        assertTrue(source.optionalCalls == 6)
        map.clear()
        assertTrue(map.isEmpty() && keys.isEmpty())
    }

    @TestAttribute
    fun nullableAndValueTypeValuesKeepPresenceDistinct() {
        val map: MutableMap<Int, Int?> = AuthoredMutableMap(mutableMapOf(1 to null, 2 to 0))
        assertTrue(map.containsKey(1) && map[1] == null)
        assertTrue(map[2] == 0 && !map.containsKey(3) && map[3] == null)
        assertTrue(map.getOrDefault(1, 42) == null && map.getOrDefault(3, 42) == 42)
        assertTrue(map.put(2, 42) == 0 && map[2] == 42)
        assertTrue(map.remove(1) == null && !map.containsKey(1))
        val numbers: MutableMap<Int, Int> = AuthoredMutableMap(mutableMapOf(1 to 2))
        assertTrue(numbers.merge(1, 3) { a, b -> a + b } == 5)
    }

    @TestAttribute
    fun erasedQueriesKeepMapIdentityAndReadOnlyMutability() {
        val source = AuthoredReadOnlyMap(mutableMapOf(1 to "one"))
        val erased: Any = source
        assertTrue(erased is Map<*, *> && erased !is MutableMap<*, *>)
        val map = erased as Map<*, *>
        assertTrue(map === source && authoredMapQuery(erased, 1) == "one" && authoredMapQuery(erased, "wrong key") == null)
        assertTrue(map.keys.contains(1) && map.values.contains("one"))
        val mutable: Any = AuthoredMutableMap(mutableMapOf(1 to "one"))
        assertTrue(mutable is MutableMap<*, *>)
    }
}
