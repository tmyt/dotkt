package roundtriptests.mapvariance

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.mapvariance.*

class MapVarianceTests {
    @TestAttribute
    fun importedMutableMapBoundRetainsSourceArityAndMutation() {
        val values = mutableMapOf("original" to 1)
        val result = fillBoundMap(values)
        assertTrue(result === values)
        assertTrue(result.remove("filled") == 23)
        assertTrue(values.size == 1)
    }

    @TestAttribute
    fun importedCovariantMapPreservesIdentityAndLiveUpdates() {
        val source = mutableMapOf("a" to "first")
        val widened = widenMap(source)
        assertTrue(widened === source)
        source["a"] = "second"
        assertTrue(widened["a"] == "second")
    }

    @TestAttribute
    fun importedNestedMapPreservesIdentityAndLiveUpdates() {
        val list = mutableListOf("first")
        val source = mutableMapOf("a" to list)
        val widened = widenNestedMap(source)
        assertTrue(widened === source)
        assertTrue(widened["a"] === list)
        list.add("second")
        assertTrue(widened["a"]!!.size == 2)
    }

    @TestAttribute
    fun importedGroupByReturnsReadableLists() {
        val grouped = groupedMap()
        assertTrue(grouped[1]!!.size == 2)
        assertTrue(grouped[1]!![0] == "a")
    }

    @TestAttribute
    fun mapStorageKeepsTheSameCovariantReference() {
        val source = mutableMapOf("a" to "first")
        val holder = MapHolder(source)
        val stored = storeMap(source)
        assertTrue(holder.value === source)
        assertTrue(stored[0] === source)
        source["a"] = "second"
        assertTrue(holder.value["a"] == "second")
        assertTrue(stored[0]["a"] == "second")
    }

    @TestAttribute
    fun erasedMapValuesStillCheckTheirClassifier() {
        val source = mutableMapOf("a" to "first")
        assertTrue(isMap(source))
        assertTrue(!isMap("not a map"))
        assertTrue(castMap(source) === source)
        var rejected = false
        try {
            castMap("not a map")
        } catch (expected: ClassCastException) {
            rejected = true
        }
        assertTrue(rejected)
    }
}
