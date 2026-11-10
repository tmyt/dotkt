import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue

private fun authoredMapQuery(source: Any, key: Any?): Any? = (source as Map<*, *>)[key]

@Suppress("UNCHECKED_CAST")
private fun <K, V> authoredMapReplace(target: MutableMap<K, V>, key: Any?, value: V): V? =
    (target as MutableMap<Any?, V>).replace(key, value)
@Suppress("UNCHECKED_CAST")
private fun <K, V> authoredMapRemoveEntry(target: MutableMap<K, V>, key: Any?, value: Any?): Boolean =
    (target as MutableMap<Any?, Any?>).remove(key, value)
@Suppress("UNCHECKED_CAST")
private fun <K, V> authoredMapReplaceEntry(target: MutableMap<K, V>, key: Any?, old: Any?, value: V): Boolean =
    (target as MutableMap<Any?, Any?>).replace(key, old, value)

open class AuthoredPlainMapOperations<K, V>(protected val data: MutableMap<K, V>) {
    var plainContainsCalls = 0
    open val size: Int get() = data.size
    open val keys: MutableSet<K> get() = data.keys
    open val values: MutableCollection<V> get() = data.values
    open val entries: MutableSet<MutableMap.MutableEntry<K, V>> get() = data.entries
    open fun isEmpty(): Boolean = data.isEmpty()
    open fun containsKey(key: K): Boolean { plainContainsCalls++; return data.containsKey(key) }
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
    var valueCalls = 0
    var lastValue: Any? = null
    var lastKey: Any? = null
    var queryFailure: IllegalStateException? = null
    private fun observeKey(key: K): Any? {
        val alias = key
        val captured: () -> Any? = { alias }
        val pending = queryFailure
        if (pending != null) throw pending
        return captured()
    }
    override val size: Int get() = backing.size
    override val keys: Set<K> = backing.keys
    override val values: Collection<V> = backing.values
    override val entries: Set<Map.Entry<K, V>> get() = backing.entries
    override fun isEmpty(): Boolean { emptyCalls++; return backing.isEmpty() }
    override fun containsKey(key: K): Boolean { containsCalls++; lastKey = observeKey(key); return backing.containsKey(key) }
    override fun containsValue(value: V): Boolean { valueCalls++; lastValue = value; return backing.containsValue(value) }
    override fun get(key: K): V? { getCalls++; lastKey = observeKey(key); return backing[key] }
    override fun getOrDefault(key: K, defaultValue: V): V {
        defaultCalls++
        return backing.getOrDefault(key, defaultValue)
    }
}

class AuthoredMutableMap<K, V>(backing: MutableMap<K, V>) : AuthoredReadOnlyMap<K, V>(backing), MutableMap<K, V> {
    var putCalls = 0
    var optionalCalls = 0
    var removeCalls = 0
    override val keys: MutableSet<K> = backing.keys
    override val values: MutableCollection<V> = backing.values
    override val entries: MutableSet<MutableMap.MutableEntry<K, V>> get() = backing.entries
    override fun put(key: K, value: V): V? { putCalls++; return backing.put(key, value) }
    override fun remove(key: K): V? { removeCalls++; lastKey = key; return backing.remove(key) }
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

private class AuthoredConcreteQueries : AuthoredReadOnlyMap<Int, String>(mutableMapOf(1 to "one")) {
    var typedCalls = 0
    var typedDefaultCalls = 0
    override fun containsKey(key: Int): Boolean { typedCalls++; return super.containsKey(key) }
    override fun getOrDefault(key: Int, defaultValue: String): String {
        typedDefaultCalls++
        return super.getOrDefault(key, defaultValue)
    }
}
private open class AuthoredQueryBound
private class AuthoredQueryItem : AuthoredQueryBound()
private class AuthoredBoundedQueries<K : AuthoredQueryBound> : AuthoredReadOnlyMap<K, Int>(mutableMapOf()) {
    var boundedCalls = 0
    override fun containsKey(key: K): Boolean { boundedCalls++; return super.containsKey(key) }
}
private class AuthoredNullableBoundedQueries<K : AuthoredQueryBound> : AuthoredReadOnlyMap<K?, Int>(mutableMapOf()) {
    var boundedCalls = 0
    override fun containsKey(key: K?): Boolean { boundedCalls++; return false }
}
private class AuthoredComparableQueries<K : Comparable<K>> : AuthoredReadOnlyMap<K, Int>(mutableMapOf()) {
    var boundedCalls = 0
    override fun containsKey(key: K): Boolean { boundedCalls++; return false }
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
        assertTrue(source.getCalls == 2 && source.lastKey == "wrong key")
        assertTrue(authoredMapQuery(erased, null) == null && source.getCalls == 3 && source.lastKey == null)
        assertTrue(!map.containsKey("wrong key") && source.containsCalls == 1 && source.lastKey == "wrong key")
        assertTrue(!map.containsKey(null) && source.containsCalls == 2 && source.lastKey == null)
        assertTrue(map.containsKey(1) && source.containsCalls == 3 && source.lastKey == 1)
        val failure = IllegalStateException("authored Map query")
        source.queryFailure = failure
        try {
            map.containsKey("throwing key")
            error("authored Map exception was suppressed")
        } catch (actual: IllegalStateException) { assertTrue(actual === failure && source.containsCalls == 4) }
        source.queryFailure = null
        assertTrue(!map.containsValue(42) && source.valueCalls == 1 && source.lastValue == 42)
        assertTrue(!map.containsValue(null) && source.valueCalls == 2 && source.lastValue == null)
        @Suppress("UNCHECKED_CAST")
        val defaultView = source as Map<Any?, String>
        assertTrue(defaultView.getOrDefault("wrong", "fallback") == "fallback" && source.defaultCalls == 1)
        assertTrue(defaultView.getOrDefault(null, "fallback") == "fallback" && source.defaultCalls == 2)
        assertTrue(map.keys.contains(1) && map.values.contains("one"))
        val mutable: Any = AuthoredMutableMap(mutableMapOf(1 to "one"))
        assertTrue(mutable is MutableMap<*, *>)
        val writes = AuthoredMutableMap(mutableMapOf(1 to "one"))
        assertTrue(authoredMapReplace(writes, "wrong", "new") == null && writes.optionalCalls == 1)
        assertTrue(!authoredMapRemoveEntry(writes, 1, 42) && writes.optionalCalls == 2)
        assertTrue(!authoredMapReplaceEntry(writes, "wrong", 42, "new") && writes.optionalCalls == 3)
        assertTrue(authoredMapReplace(writes, 1, "new") == "one" && writes.optionalCalls == 4 && writes[1] == "new")
        val removalView: MutableMap<*, *> = writes
        assertTrue(removalView.remove("wrong") == null && writes.removeCalls == 1 && writes.lastKey == "wrong")
        assertTrue(removalView.remove(null) == null && writes.removeCalls == 2 && writes.lastKey == null)

        val nullable = AuthoredReadOnlyMap(mutableMapOf<Int?, Int?>(null to null, 1 to 7))
        val nullableView: Map<*, *> = nullable
        assertTrue(nullableView.containsKey(null) && nullable.containsCalls == 1)
        assertTrue(nullableView[null] == null && nullable.getCalls == 1)
        assertTrue(!nullableView.containsKey("wrong") && nullable.containsCalls == 2 && nullable.lastKey == "wrong")

        val typed = AuthoredConcreteQueries()
        val typedView: Map<*, *> = typed
        assertTrue(!typedView.containsKey("wrong") && !typedView.containsKey(null) && typed.typedCalls == 0)
        assertTrue(typedView.containsKey(1) && typed.typedCalls == 1)
        @Suppress("UNCHECKED_CAST")
        val typedDefaults = typed as Map<Any?, String>
        assertTrue(typedDefaults.getOrDefault("wrong", "fallback") == "fallback" && typed.typedDefaultCalls == 0)
        assertTrue(typedDefaults.getOrDefault(null, "fallback") == "fallback" && typed.typedDefaultCalls == 0)
        assertTrue(typedDefaults.getOrDefault(1, "fallback") == "one" && typed.typedDefaultCalls == 1)
        val bounded = AuthoredBoundedQueries<AuthoredQueryItem>()
        val boundedView: Map<*, *> = bounded
        assertTrue(!boundedView.containsKey("wrong") && !boundedView.containsKey(null) && bounded.boundedCalls == 0)
        assertTrue(!boundedView.containsKey(AuthoredQueryBound()) && bounded.boundedCalls == 1)
        val nullableBounded = AuthoredNullableBoundedQueries<AuthoredQueryItem>()
        val nullableBoundedView: Map<*, *> = nullableBounded
        assertTrue(!nullableBoundedView.containsKey("wrong") && nullableBounded.boundedCalls == 0)
        assertTrue(!nullableBoundedView.containsKey(null) && nullableBounded.boundedCalls == 1)
        assertTrue(!nullableBoundedView.containsKey(AuthoredQueryBound()) && nullableBounded.boundedCalls == 2)
        val comparable = AuthoredComparableQueries<Int>()
        val comparableView: Map<*, *> = comparable
        assertTrue(!comparableView.containsKey(AuthoredQueryBound()) && comparable.boundedCalls == 0)
        assertTrue(!comparableView.containsKey("other") && comparable.boundedCalls == 1)
        assertTrue(!comparableView.containsKey(null) && comparable.boundedCalls == 1)
        val inheritedNullable = AuthoredPlainInheritedMap(mutableMapOf<Int?, String>(null to "present"))
        val inheritedView: Map<*, *> = inheritedNullable
        assertTrue(inheritedView.containsKey(null) && inheritedNullable.plainContainsCalls == 1)
        assertTrue(!inheritedView.containsKey("wrong") && inheritedNullable.plainContainsCalls == 2)
    }
}
