package roundtriptests.mapvariance

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.mapvariance.*

interface ImportedSource<out T> : CovariantSource<T>
interface ImportedSink<in T> : ContravariantSink<T>

class MapVarianceTests {
    @TestAttribute
    fun physicalFlagsRetainLegalImportedVarianceButNotInvariantMapVariance() {
        val producer = System.Reflection.Assembly.Load("RoundtripProducer")
        val map = producer.GetType("roundtrip.mapvariance.ProjectedMap`2", true)!!
        assertTrue(map.GetGenericArguments()[1].GenericParameterAttributes == System.Reflection.GenericParameterAttributes.None)
        val consumer = System.Reflection.Assembly.GetExecutingAssembly()
        val source = consumer.GetType("roundtriptests.mapvariance.ImportedSource`1", true)!!
        val sink = consumer.GetType("roundtriptests.mapvariance.ImportedSink`1", true)!!
        assertTrue(source.GetGenericArguments()[0].GenericParameterAttributes == System.Reflection.GenericParameterAttributes.Covariant)
        assertTrue(sink.GetGenericArguments()[0].GenericParameterAttributes == System.Reflection.GenericParameterAttributes.Contravariant)
    }

    @TestAttribute
    fun inheritedInvariantNativeFaceRetainsKotlinCovarianceAcrossDlls() {
        System.Reflection.Assembly.Load("RoundtripProducer").GetTypes()
        val narrow = projectedTextMap("one")
        val wide: ProjectedMap<String, Any> = narrow
        assertTrue(wide === narrow)
        assertTrue(wide["present"] == "one")
    }

    @TestAttribute
    fun defaultMapsLoadAndRetainReadAndMutableBehavior() {
        System.Reflection.Assembly.Load("DotKt.Stdlib").GetTypes()
        val values = mapOf("present" to 1).withDefault { 42 }
        assertTrue(values.getValue("present") == 1)
        assertTrue(values.getValue("absent") == 42)
        val mutable = mutableMapOf("present" to 2)
        val defaults = mutable.withDefault { 43 }
        defaults["added"] = 3
        assertTrue(mutable["added"] == 3)
        assertTrue(defaults.getValue("present") == 2)
        assertTrue(defaults.getValue("absent") == 43)
        val narrow = defaultTextMap()
        val wide: Map<String, Any> = narrow
        assertTrue(wide === narrow)
        assertTrue(wide.getValue("absent") == "fallback")
    }

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
