import NUnit.Framework.TestAttribute
import roundtrip.upstreammutablemap.UpstreamProducedMutableMap
import roundtrip.upstreammutablemap.upstreamProducedMapText

class CrossModuleUpstreamMutableMapTests {
    @TestAttribute
    fun producerMapInheritsCommonPolicyAcrossDllBoundaries() {
        val produced = UpstreamProducedMutableMap<String, Int>()
        val map: MutableMap<String, Int> = produced
        map["a"] = 1
        check(upstreamProducedMapText(produced) == "{a=1}")
        check(produced.toString() == "{a=1}")
        check((produced as Any) is AbstractMap<*, *>)
        check(produced.size == 1 && produced.containsKey("a") && produced.get("a") == 1)
        check(map == mapOf("a" to 1) && map.hashCode() == mapOf("a" to 1).hashCode())
        val keys = map.keys
        val values = map.values
        map["b"] = 2
        check(keys.size == 2 && values.contains(2))
        check(keys.remove("b") && !produced.containsKey("b"))
        check(values.remove(1) && produced.isEmpty())
    }

    @TestAttribute
    fun importedInheritedMembersKeepNullableOwnerFrames() {
        val produced = UpstreamProducedMutableMap<Int, Int?>()
        val map: MutableMap<Int, Int?> = produced
        map[1] = null
        check(produced.containsKey(1) && produced.get(1) == null)
        check(upstreamProducedMapText(produced) == "{1=null}")
        val keys = map.keys
        val values = map.values
        map[1] = 0
        check(produced.get(1) == 0 && values.contains(0))
        check(produced.remove(1) == 0 && keys.isEmpty())
        map[2] = 7
        produced.clear()
        check(produced.isEmpty() && keys.isEmpty() && values.isEmpty())
    }
}
