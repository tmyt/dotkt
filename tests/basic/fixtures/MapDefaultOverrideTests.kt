import NUnit.Framework.TestAttribute

private open class DefaultOverrideMap : AbstractMutableMap<String, Int>() {
    private val backing = hashMapOf("present" to 1)
    var defaultCalls = 0
    var removeCalls = 0
    override val entries: MutableSet<MutableMap.MutableEntry<String, Int>> get() = backing.entries
    override fun put(key: String, value: Int): Int? = backing.put(key, value)
    override fun getOrDefault(key: String, defaultValue: Int): Int { defaultCalls++; return 42 }
    override fun remove(key: String, value: Int): Boolean { removeCalls++; return true }
}

private class InheritedDefaultOverrideMap : DefaultOverrideMap() {
    override fun getOrDefault(key: String, defaultValue: Int): Int { defaultCalls++; return 73 }
    override fun remove(key: String, value: Int): Boolean { removeCalls++; return false }
}

class MapDefaultOverrideTests {
    @TestAttribute
    fun interfaceCallsDispatchAuthoredDefaultOverrides() {
        val actual = DefaultOverrideMap()
        val read: Map<String, Int> = actual
        val write: MutableMap<String, Int> = actual
        check(read.getOrDefault("absent", 0) == 42)
        check(write.remove("absent", 0))
        check(actual.defaultCalls == 1)
        check(actual.removeCalls == 1)
    }

    @TestAttribute
    fun inheritedBridgesDispatchMostDerivedOverride() {
        val actual = InheritedDefaultOverrideMap()
        val read: Map<String, Int> = actual
        val write: MutableMap<String, Int> = actual
        check(read.getOrDefault("absent", 0) == 73)
        check(!write.remove("absent", 0))
        check(actual.defaultCalls == 1)
        check(actual.removeCalls == 1)
    }

    @TestAttribute
    fun foreignMapsRetainOrdinaryDefaultBehavior() {
        val map = hashMapOf<String, Int?>("present" to 1, "null" to null)
        check(map.getOrDefault("absent", 42) == 42)
        check(map.getOrDefault("null", 42) == null)
        check(!map.remove("present", 2))
        check(map.remove("present", 1))
        check(!map.containsKey("present"))
    }
}
