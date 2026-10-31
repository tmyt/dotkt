import NUnit.Framework.TestAttribute

private class RestoredMutableMap<K, V> : AbstractMutableMap<K, V>() {
    private val storage = mutableMapOf<K, V>()
    override val entries: MutableSet<MutableMap.MutableEntry<K, V>> get() = storage.entries
    override fun put(key: K, value: V): V? = storage.put(key, value)
}
private fun <T> upstreamMutableMapText(value: T): String = value.toString()

class UpstreamMutableMapTests {
    @TestAttribute
    fun inheritedMapPolicyWorksThroughGenericAndOpaqueValues() {
        val map = RestoredMutableMap<String, Int>()
        map["a"] = 1
        check(map.size == 1 && !map.isEmpty() && map["a"] == 1)
        check(map.containsKey("a") && map.containsValue(1) && !map.containsKey("missing"))
        check(upstreamMutableMapText(map) == "{a=1}" && (map as Any).toString() == "{a=1}")
        check(map.toString() == "{a=1}")
        check((map as Any) is AbstractMap<*, *>)
        check(map == mapOf("a" to 1) && mapOf("a" to 1) == map)
        check(map.hashCode() == mapOf("a" to 1).hashCode())
        check(!map.equals(mapOf(1 to "a")) && !map.equals(mapOf("a" to null)))
    }

    @TestAttribute
    fun cachedMutableViewsKeepTheirOwningGenericMapAndRemainLive() {
        val map = RestoredMutableMap<String, Int>()
        map["a"] = 1
        val keys = map.keys
        val values = map.values
        check(keys === map.keys && values === map.values)
        map["b"] = 2
        check(keys.size == 2 && keys.contains("b") && values.contains(2))
        map.entries.single { it.key == "a" }.setValue(3)
        check(values.contains(3) && !values.contains(1))
        check(keys.remove("b") && !map.containsKey("b"))
        check(values.remove(3) && map.isEmpty())
        map.putAll(mapOf("c" to 4, "d" to 5))
        val cursor = keys.iterator()
        cursor.next()
        cursor.remove()
        check(map.size == 1 && keys.size == 1)
        values.clear()
        check(map.isEmpty() && keys.isEmpty())
    }

    @TestAttribute
    fun inheritedReadsAndRemovalsRetainNullableValueFrames() {
        val map = RestoredMutableMap<Int, Int?>()
        check(map.put(1, null) == null && map.containsKey(1))
        check(map[1] == null && map.containsValue(null))
        check(map.remove(1) == null && !map.containsKey(1))
        check(map.put(2, 0) == null && map.put(2, 7) == 0)
        check(map[2] == 7 && map.remove(2) == 7 && map.isEmpty())
        map[3] = null
        val values = map.values
        check(values.contains(null))
        map[3] = 0
        check(values.contains(0) && !values.contains(null))
        val readonly: Map<Int, Any?> = map
        check(readonly[3] == 0 && readonly[4] == null)
    }

    @TestAttribute
    fun emptyAndBulkOperationsUseEntryBackedMutableViews() {
        val map = RestoredMutableMap<String, Int>()
        check(map.isEmpty() && map.keys.isEmpty() && map.values.isEmpty())
        check(upstreamMutableMapText(map) == "{}")
        map.putAll(mapOf("a" to 1, "b" to 2))
        check(map.size == 2 && map["a"] == 1 && map["b"] == 2)
        var keyRejected = false
        try { map.keys.add("c") } catch (_: UnsupportedOperationException) { keyRejected = true }
        var valueRejected = false
        try { map.values.add(3) } catch (_: UnsupportedOperationException) { valueRejected = true }
        check(keyRejected && valueRejected && map.size == 2)
        map.keys.clear()
        check(map.isEmpty())
        map["c"] = 3
        map.clear()
        check(map.isEmpty() && map.values.isEmpty())
    }
}
