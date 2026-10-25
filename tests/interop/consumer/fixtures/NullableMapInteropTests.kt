import NUnit.Framework.TestAttribute
import NullableMapInterop.NativeMaps

private class CopySourceMap<K, V>(private val backing: Map<K, V>) : Map<K, V> by backing

class NullableMapInteropTests {
    @TestAttribute
    fun nativeDictionaryFacesShareOriginalNullableKeys() {
        NativeMaps.VerifyFaces(linkedMapOf<String?, String?>(null to "null", "key" to null))
        NativeMaps.VerifyFaces(hashMapOf<String?, String?>(null to "null", "key" to null))
        NativeMaps.VerifyFaces(mutableMapOf<String?, String?>(null to "null", "key" to null))
        val native = System.Collections.Generic.Dictionary<String, Int>()
        native["key"] = 1
        check(native.Count == 1 && native["key"] == 1)
        val ordered = System.Collections.Generic.OrderedDictionary<String, Int>()
        ordered["a"] = 1
        ordered["b"] = 2
        ordered.Remove("a")
        ordered["c"] = 3
        check(ordered.Count == 2 && ordered["b"] == 2)
        val nullable: Any = linkedMapOf<String?, String?>(null to "null")
        val nativePeer = System.Collections.Generic.Dictionary<String, String>()
        nativePeer["key"] = "null"
        val orderedPeer = System.Collections.Generic.OrderedDictionary<String, String>()
        orderedPeer["key"] = "null"
        check(nullable != (nativePeer as Any) && (nativePeer as Any) != nullable)
        check(nullable != (orderedPeer as Any) && (orderedPeer as Any) != nullable)
    }

    @TestAttribute
    fun nativeCopyHelpersInferExactTypesAndNeverShareMutations() {
        val source = linkedMapOf<String?, String?>("a" to "one", "b" to null)
        check(NativeMaps.Use(source.toClrDictionary()) == 2)
        check(NativeMaps.UseOrdered(source.toClrOrderedDictionary()) == 2)
        check(NativeMaps.Use(CopySourceMap(source).toClrDictionary()) == 2)
        check(NativeMaps.Use(emptyMap<String?, String>().toClrDictionary()) == 0)
        check(NativeMaps.UseNumbers(mapOf<Int?, String>(1 to "one").toClrDictionary()) == 1)
        check(NativeMaps.UsePrimitiveValues(mapOf("value" to 7).toClrDictionary()) == 7)
        val copy = source.toClrDictionary()
        val ordered = source.toClrOrderedDictionary()
        source["a"] = "source"
        copy["a"] = "copy"
        check(source["a"] == "source" && ordered["a"] == "one")
        check(copy["b"] == null && copy.ContainsKey("b"))
        check(NativeMaps.UseFloats(hashMapOf(0.0 to "positive", -0.0 to "negative").toClrDictionary()) == 2)
        val element: Any = mutableListOf(1)
        check(NativeMaps.FirstValue(mapOf("value" to element).toClrDictionary()) === element)
        source[null] = "null"
        var rejected = 0
        try { source.toClrDictionary() } catch (e: IllegalArgumentException) { check(e.message!!.contains("null key")); rejected++ }
        try { source.toClrOrderedDictionary() } catch (e: IllegalArgumentException) { check(e.message!!.contains("null key")); rejected++ }
        check(rejected == 2 && source.containsKey(null))
    }
}
