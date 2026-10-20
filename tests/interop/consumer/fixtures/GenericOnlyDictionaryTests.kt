import NUnit.Framework.TestAttribute
import DictionaryContracts.GenericOnlyDictionary
import DictionaryContracts.DualDictionary
import DictionaryContracts.NativeDictionaries

private fun dictionaryStrings(): GenericOnlyDictionary<String, String> {
    val native = GenericOnlyDictionary<String, String>()
    native.Add("key", "value")
    return native
}

private fun projectedDictionarySize(map: Map<*, *>): Int = map.size

private class DictionaryGenericFrame<V> {
    fun <K> get(map: Map<K, V>, key: K): V? = map[key]
}

class GenericOnlyDictionaryTests {
    @TestAttribute fun classifiersAndCastsPreserveTheOriginalDictionary() {
        val value: Any = dictionaryStrings()
        check(value is Map<*, *> && value is MutableMap<*, *>)
        check((value as? Map<*, *>) === value)
        check((value as Map<String, String>) === value)
        val nullable: Any? = value
        check((nullable as Map<*, *>?) === value)
        check((null as Any?) is Map<*, *>?)
        check((null as Any?) as? Map<*, *> == null)
        val notMap: Any = "not a map"
        check(notMap !is Map<*, *> && (notMap as? Map<*, *>) == null)
        var rejected = false
        try { notMap as Map<*, *> } catch (_: ClassCastException) { rejected = true }
        check(rejected)
    }

    @TestAttribute fun readsUseTheGenericDictionaryContract() {
        val value: Any = dictionaryStrings()
        val map = value as Map<String, String>
        check(map.size == 1 && projectedDictionarySize(map) == 1 && !map.isEmpty())
        check(map["key"] == "value" && map["missing"] == null)
        check(map.containsKey("key") && !map.containsKey("missing"))
        check(map.containsValue("value") && !map.containsValue("absent"))
        check(map.getOrDefault("missing", "default") == "default")
        check(map.keys.single() == "key" && map.values.single() == "value")
        check(map.entries.single().key == "key" && map.entries.single().value == "value")
        check(map.toString() == "{key=value}")
    }

    @TestAttribute fun mutationsRetainKotlinReturnConventions() {
        val value: Any = dictionaryStrings()
        val map = value as MutableMap<String, String>
        check(map.put("key", "changed") == "value")
        check(map.put("new", "created") == null)
        check(map.remove("new") == "created" && map.remove("missing") == null)
        check(map.putIfAbsent("key", "ignored") == "changed")
        check(map.replace("key", "replaced") == "changed")
        check(map.replace("key", "replaced", "again"))
        check(!map.remove("key", "wrong"))
        check(map.merge("key", "!") { old, next -> old + next } == "again!")
        check(map.remove("key", "again!"))
        val other: Any = dictionaryStrings()
        map.putAll(other as Map<String, String>)
        check(map["key"] == "value")
        map.clear()
        check(map.isEmpty() && map.size == 0)
    }

    @TestAttribute fun widenedReadViewsDoNotRequireInvariantClrCasts() {
        val native = GenericOnlyDictionary<String, Int>()
        native.Add("key", 42)
        val map: Map<String, Int> = (native as Any) as Map<String, Int>
        val widened: Map<String, Any> = map
        check(widened["key"] == 42 && widened.size == 1)
        val unchecked = (map as Any) as Map<Any, Any>
        check(!unchecked.containsKey(42) && unchecked[42] == null)
        check(widened.values.single() == 42 && widened.entries.single().value == 42)
    }

    @TestAttribute fun mutableViewsStayLiveAndUseTheSameBackingDictionary() {
        val native = dictionaryStrings()
        val map = (native as Any) as MutableMap<String, String>
        val keys = map.keys
        val values = map.values
        val entry = map.entries.single()
        check(entry.setValue("changed") == "value" && map["key"] == "changed")
        map["new"] = "extra"
        check(keys.size == 2 && values.size == 2)
        check(values.remove("extra") && !map.containsKey("new"))
        check(keys.remove("key") && map.isEmpty())
    }

    @TestAttribute fun exactStaticClosureSelectsAmongMultipleDictionaryContracts() {
        val native = DualDictionary()
        native.Add("key", "text")
        val value: Any = native
        val texts = value as Map<String, String>
        val numbers = value as MutableMap<Int, Int>
        check(texts.size == 1 && texts["key"] == "text")
        check(numbers.size == 2 && numbers[7] == 42)
        val copy = (GenericOnlyDictionary<Int, Int>() as Any) as MutableMap<Int, Int>
        copy.putAll(numbers)
        check(copy.size == 2 && copy[7] == 42)
        check(numbers.put(7, 99) == 42 && texts["key"] == "text")
        check(numbers.keys.size == 2 && numbers.values.contains(99))
        numbers.clear()
        check(numbers.isEmpty() && texts.size == 1)
        var rejected = false
        try { projectedDictionarySize(value as Map<*, *>) }
        catch (_: System.InvalidOperationException) { rejected = true }
        check(rejected)
    }

    @TestAttribute fun nativeTargetInvocationExceptionsAreNotDoubleWrapped() {
        val native = dictionaryStrings()
        val failure = System.Reflection.TargetInvocationException(System.Exception("native"))
        native.CountFailure = failure
        var caught: Any? = null
        try { ((native as Any) as Map<*, *>).size }
        catch (e: System.Exception) { caught = e }
        check(caught === failure)
    }

    @TestAttribute fun genericDictionaryEnumeratorsAreDisposedIncludingEarlyMatches() {
        val native = dictionaryStrings()
        val map = (native as Any) as Map<String, String>
        check(map.keys.single() == "key")
        check(native.EnumeratorsDisposed == 1)
        check(map.containsValue("value"))
        check(native.EnumeratorsDisposed == 2)
        check(map.entries.single().value == "value")
        check(native.EnumeratorsDisposed == 3)
    }

    @TestAttribute fun rawAndBclDictionaryPathsRemainUsable() {
        val raw = NativeDictionaries.Raw() as MutableMap<*, *>
        check(raw.size == 1 && raw["key"] == "raw")
        raw.clear()
        check(raw.isEmpty())
        val generic = NativeDictionaries.Generic() as MutableMap<String, Int>
        check(generic.size == 1 && generic["key"] == 42)
        check(generic.put("key", 43) == 42)
        check(generic.entries.single().value == 43)
    }

    @TestAttribute fun nullableValuesAndCallerGenericFramesRetainTheirContracts() {
        val native = GenericOnlyDictionary<String, Int?>()
        native.Add("null", null)
        native.Add("value", 42)
        val map = (native as Any) as MutableMap<String, Int?>
        val frame = DictionaryGenericFrame<Int?>()
        check(map.size == 2 && map.containsKey("null") && map["null"] == null)
        check(frame.get(map, "value") == 42 && frame.get(map, "missing") == null)
        check(map.put("value", null) == 42)
        check(map.put("value", 43) == null && frame.get(map, "value") == 43)
    }
}
