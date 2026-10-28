import NUnit.Framework.TestAttribute

private class SetObservedMap<K, V> : AbstractMutableMap<K, V>() {
    private val backing = linkedMapOf<K, V>()
    var writes = 0
    override val entries: MutableSet<MutableMap.MutableEntry<K, V>> get() = backing.entries
    override fun put(key: K, value: V): V? { writes++; return backing.put(key, value) }
}

private fun <K, V> storeMapValue(target: MutableMap<K, V>, key: K, value: V) { target[key] = value }

private class SetOwner<K> {
    fun <V> store(target: MutableMap<K, V>, key: K, value: V) { target[key] = value }
}

private class SetCollisionKey(val id: Int) {
    override fun equals(other: Any?): Boolean = other is SetCollisionKey && id == other.id
    override fun hashCode(): Int = 0
}

class MapSetAbiTests {
    @TestAttribute
    fun ordinaryAndGenericSetCallsPreserveAliasedMapDispatch() {
        val observed = SetObservedMap<Int, String>()
        val alias: MutableMap<Int, String> = observed
        observed[7] = "first"
        storeMapValue(alias, 7, "second")
        SetOwner<Int>().store(alias, 9, "third")
        check(alias === observed && observed.writes == 3 && alias.size == 2)
        check(alias[7] == "second" && alias[9] == "third")

        val optional = SetObservedMap<String?, Int?>()
        storeMapValue(optional, null, null)
        SetOwner<String?>().store(optional, null, 7)
        storeMapValue(optional, "absent-value", null)
        check(optional.writes == 3 && optional.size == 2 && optional[null] == 7)
        check(optional.containsKey("absent-value") && optional["absent-value"] == null)

        val durations = SetObservedMap<String, System.TimeSpan>()
        SetOwner<String>().store(durations, "duration", System.TimeSpan(0, 0, 5))
        check(durations.writes == 1 && durations["duration"]!!.TotalSeconds == 5.0)
    }

    @TestAttribute
    fun setRetainsKotlinKeyEqualityAndNestedGenericValues() {
        val keys = SetObservedMap<SetCollisionKey, String>()
        val first = SetCollisionKey(1)
        storeMapValue(keys, first, "first")
        SetOwner<SetCollisionKey>().store(keys, SetCollisionKey(1), "replacement")
        storeMapValue(keys, SetCollisionKey(2), "collision")
        check(keys.size == 2 && keys.writes == 3)
        check(keys.keys.first() === first && keys[SetCollisionKey(1)] == "replacement")

        val nested = SetObservedMap<List<Int>, Pair<String, Int?>>()
        val value = "value" to null
        storeMapValue(nested, listOf(1, 2), value)
        check(nested[listOf(1, 2)] === value && nested.writes == 1)
        SetOwner<List<Int>>().store(nested, listOf(1, 2), "updated" to 7)
        check(nested.size == 1 && nested.writes == 2 && nested[listOf(1, 2)]!!.second == 7)
    }
}
