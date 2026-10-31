import NUnit.Framework.TestAttribute
import AuthoredMapInterop.NativeMaps

private class UpstreamNativeMutableMap<K, V> : AbstractMutableMap<K, V>() {
    private val storage = mutableMapOf<K, V>()
    override val entries: MutableSet<MutableMap.MutableEntry<K, V>> get() = storage.entries
    override fun put(key: K, value: V): V? = storage.put(key, value)
}

class UpstreamMutableMapInteropTests {
    @TestAttribute
    fun inheritedPolicyRetainsNativeDictionaryMutationAndCopyContracts() {
        val readable = UpstreamNativeMutableMap<String, Int>()
        readable["a"] = 1
        check(NativeMaps.AbstractMap(readable))
        val mutable = UpstreamNativeMutableMap<Int, Int>()
        mutable[1] = 0
        check(NativeMaps.Echo(mutable) === mutable)
        check(NativeMaps.Mutable(mutable))
        check(mutable.isEmpty())
        val nullable: Any = hashMapOf<String?, String?>(null to null, "key" to "value")
        val foreign = NullableMapInterop.NativeMaps.NullableKeyDictionaryView(nullable)
        check(nullable == foreign && foreign == nullable)
    }

    @TestAttribute
    fun inheritedMapQueriesAndViewsRetainTheirNativeFrames() {
        val map = UpstreamNativeMutableMap<Int, String>()
        map[7] = "seven"
        check(NativeMaps.CopyArguments(map))
        val readonly: Map<Int, Any> = map
        check(readonly[7] == "seven" && readonly[8] == null)
        check(!map.equals(mapOf("7" to "seven")))
        val keys = map.keys
        val values = map.values
        map[8] = "eight"
        check(keys.contains(8) && values.contains("eight"))
        check(keys.remove(7) && map.size == 1 && !values.contains("seven"))
        values.clear()
        check(map.isEmpty() && keys.isEmpty())
    }
}
