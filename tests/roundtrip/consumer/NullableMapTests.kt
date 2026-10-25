package roundtriptests.nullablemap

import NUnit.Framework.TestAttribute
import roundtrip.nullablemap.*

class NullableMapTests {
    @TestAttribute
    fun actualMapsAndNativeCopiesCrossDllBoundaries() {
        val map = makeNullableMap()
        check(map[null] == "null" && map.containsKey("key") && map["key"] == null)
        check(map.put(null, "changed") == "null" && map.size == 2)
        val ordered = makeOrderedNullableMap()
        val keys = ordered.keys
        ordered.remove(null)
        ordered["new"] = "new"
        ordered[null] = "last"
        check(keys.toList() == listOf("key", "new", null))
        val copy = copyNativeMap(mapOf("key" to "value"))
        check(copy.Count == 1 && copy["key"] == "value")
        copy["key"] = "copy"
        check(map[null] == "changed")
    }
}
