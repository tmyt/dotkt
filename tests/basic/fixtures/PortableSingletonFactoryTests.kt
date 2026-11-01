@file:Suppress("INVISIBLE_MEMBER", "INVISIBLE_REFERENCE")

package portablesingletonfactories

import NUnit.Framework.TestAttribute

class PortableSingletonFactoryTests {
    @TestAttribute fun capacityHintsAreElementCountsWithoutOverflow() {
        for (size in listOf(Int.MIN_VALUE, -1, 0, 1, 2, 3, 100, (1 shl 30) - 1, 1 shl 30, Int.MAX_VALUE)) {
            check(kotlin.collections.mapCapacity(size) == size)
        }
    }

    @TestAttribute fun singletonFactoriesPreserveNullsAndNativeFaces() {
        val pair: Pair<String?, String?> = null to null
        val map = mapOf(pair)
        check(map.size == 1 && map.containsKey(null) && map[null] == null)
        check(map.keys.single() == null && map.values.single() == null)
        val nativeMap = (mapOf("native" to "value") as Any) as System.Collections.IDictionary
        check(nativeMap.Count == 1 && nativeMap.Contains("native") && nativeMap["native"] == "value")
        val set = setOf<String?>(null)
        check(set.size == 1 && null in set && set.single() == null)
        val nativeSet = (set as Any) as System.Collections.Generic.ICollection<String?>
        check(nativeSet.Count == 1 && nativeSet.Contains(null))
    }

    @TestAttribute fun singletonCopiesHaveIndependentEntriesAndStorage() {
        val source = linkedMapOf<String?, Int?>(null to null)
        val copy = source.toMap()
        val entry = source.entries.single()
        entry.setValue(7)
        check(copy.size == 1 && copy.containsKey(null) && copy[null] == null)
        source.clear()
        check(copy.size == 1 && copy.entries.single().key == null)
        val ordinary = linkedMapOf("key" to "before")
        val readonly = ordinary.toMap()
        ordinary["key"] = "after"
        check(readonly["key"] == "before")
    }

    @TestAttribute fun sharedFactoriesKeepDuplicateAndInsertionOrderPolicy() {
        val map = mapOf("b" to 1, "a" to 2, "b" to 3, "c" to 4)
        check(map.keys.toList() == listOf("b", "a", "c") && map["b"] == 3)
        check(setOf("b", "a", "b", "c").toList() == listOf("b", "a", "c"))
        for (capacity in 0..4) {
            val builtMap = buildMap<String?, Int>(capacity) { put("b", 1); put(null, 2); put("a", 3); put("b", 4) }
            check(builtMap.keys.toList() == listOf("b", null, "a") && builtMap["b"] == 4)
            val builtSet = buildSet<String?>(capacity) { add("b"); add(null); add("a"); add("b") }
            check(builtSet.toList() == listOf("b", null, "a"))
        }
    }

    @TestAttribute fun negativeCapacitiesFailBeforeRunningBuilderActions() {
        var actions = 0
        for (capacity in listOf(-1, Int.MIN_VALUE)) {
            var mapFailed = false
            try { buildMap<Int, Int>(capacity) { actions++; put(1, 2) } }
            catch (_: IllegalArgumentException) { mapFailed = true }
            var setFailed = false
            try { buildSet<Int>(capacity) { actions++; add(1) } }
            catch (_: IllegalArgumentException) { setFailed = true }
            check(mapFailed && setFailed && actions == 0)
        }
        var inputs = ""
        fun key(): String { inputs += "k"; return "key" }
        fun value(): Int { inputs += "v"; return 7 }
        check(mapOf(key() to value())["key"] == 7 && inputs == "kv")
        inputs = ""
        check(setOf(key()).single() == "key" && inputs == "k")
    }
}
