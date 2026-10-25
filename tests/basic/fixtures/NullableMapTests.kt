import NUnit.Framework.TestAttribute

private class CollidingMapKey(val id: Int) {
    override fun equals(other: Any?): Boolean = other is CollidingMapKey && id == other.id
    override fun hashCode(): Int = 0
}

private class ReadonlyMapEntry(override val key: String?, override val value: String?) : Map.Entry<String?, String?> {
    override fun equals(other: Any?): Boolean = other is Map.Entry<*, *> && key == other.key && value == other.value
    override fun hashCode(): Int = (key?.hashCode() ?: 0) xor (value?.hashCode() ?: 0)
}

private fun exerciseNullableMap(map: MutableMap<String?, Int?>) {
    map.clear()
    val keys = map.keys
    val values = map.values
    val entries = map.entries
    val read: Map<String?, Int?> = map
    check(!map.containsKey(null) && map[null] == null)
    check(map.put(null, 1) == null)
    check(map.containsKey(null) && read[null] == 1 && map.size == 1)
    check(keys.contains(null) && values.contains(1) && entries.single().key == null)
    check(map.put(null, 2) == 1 && map.size == 1)
    check(entries.single().setValue(null) == 2 && map.containsKey(null))
    check(map[null] == null && read.getOrDefault(null, 42) == null)
    check(map.containsValue(null) && values.contains(null))
    check(map.remove(null) == null && !map.containsKey(null) && entries.isEmpty())
    check(map.remove(null) == null)
    map["a"] = 3
    map[null] = 4
    check(keys.size == 2 && entries.size == 2 && values.size == 2)
    check(keys.remove(null) && map.size == 1 && !map.containsKey(null))
    map[null] = null
    check(values.remove(null) && !map.containsKey(null))
    map[null] = 5
    val iterator = entries.iterator()
    while (iterator.hasNext()) if (iterator.next().key == null) iterator.remove()
    check(!map.containsKey(null) && map["a"] == 3)
    map[null] = 6
    entries.clear()
    check(map.isEmpty() && keys.isEmpty() && values.isEmpty())
}

class NullableMapTests {
    @TestAttribute
    fun factoriesAndConstructorsRetainNullableKeys() {
        check(mutableMapOf<String?, Int>(null to 1)[null] == 1)
        check(hashMapOf<String?, Int>(null to 1)[null] == 1)
        check(LinkedHashMap<String?, Int>().also { it[null] = 1 }[null] == 1)
        val pair: Pair<String?, Int?> = null to null
        val source = mapOf(pair)
        check(source.containsKey(null) && source[null] == null)
        for (map in listOf<MutableMap<String?, Int?>>(mutableMapOf(pair), hashMapOf(pair), linkedMapOf(pair),
            HashMap(), HashMap(4), HashMap(4, 0.5f), HashMap(source),
            LinkedHashMap(), LinkedHashMap(4), LinkedHashMap(4, 0.5f), LinkedHashMap(source))) {
            exerciseNullableMap(map)
        }
        check(buildMap<String?, Int> { put(null, 7) }[null] == 7)
        val erased: Any = mutableMapOf<String?, Int>(null to 9)
        check((erased as Map<*, *>)[null] == 9)
        check(emptyMap<String?, Int>()[null] == null)
        for (map in listOf<MutableMap<String?, String?>>(HashMap(), LinkedHashMap())) {
            map[null] = "null"
            map["key"] = null
            val read: Map<String?, String?> = map
            val nullEntry: Map.Entry<String?, String?> = ReadonlyMapEntry(null, "null")
            val valueEntry: Map.Entry<String?, String?> = ReadonlyMapEntry("key", null)
            check(read.entries.contains(nullEntry) && map.entries.contains(valueEntry))
            check(read.entries.containsAll(listOf(nullEntry, valueEntry)))
            val wrong: Map.Entry<String?, String?> = ReadonlyMapEntry(null, "wrong")
            check(!read.entries.contains(wrong))
            check(!map.entries.remove(wrong))
            check(map.entries.remove(nullEntry) && !map.containsKey(null))
            check(map.entries.remove(valueEntry) && map.isEmpty())
            map[null] = "again"
            check(map.entries.single().setValue(null) == "again" && map[null] == null)
        }
    }

    @TestAttribute
    fun nullableKeysKeepInsertionOrderAndLiveViews() {
        val map = linkedMapOf<String?, Int?>("a" to 1, null to 2, "b" to null)
        val read: Map<String?, Int?> = map
        val keys = read.keys
        val values = read.values
        val entries = read.entries
        map[null] = 3
        check(keys.toList() == listOf("a", null, "b"))
        check(values.toList() == listOf(1, 3, null))
        check(entries.map { it.key } == keys.toList())
        map.remove("a")
        map["c"] = 4
        check(keys.toList() == listOf(null, "b", "c"))
        map.remove(null)
        map[null] = 5
        check(keys.toList() == listOf("b", "c", null))
        check(map.toString() == "{b=null, c=4, null=5}")
        check(map == linkedMapOf("b" to null, "c" to 4, null to 5))
        check(map.hashCode() == linkedMapOf("b" to null, "c" to 4, null to 5).hashCode())
        val iterator = map.keys.iterator()
        iterator.next()
        map["added"] = 6
        var invalidated = false
        try { iterator.hasNext() } catch (error: ConcurrentModificationException) { invalidated = true }
        check(invalidated)
    }

    @TestAttribute
    fun primitiveAndNullablePrimitiveKeysUseKotlinEquality() {
        val numbers = hashMapOf<Int?, String>(null to "null", 0 to "zero", 42 to "answer")
        check(numbers[null] == "null" && numbers[0] == "zero" && numbers[42] == "answer")
        check(numbers.put(null, "changed") == "null" && numbers.size == 3)
        check(numbers.remove(null) == "changed" && numbers.size == 2)
        val floats = hashMapOf(0.0 to "positive", -0.0 to "negative")
        check(floats.size == 2 && floats[0.0] == "positive" && floats[-0.0] == "negative")
        check(hashMapOf(1 to "one", 2 to "two")[2] == "two")
        val original = CollidingMapKey(1)
        val equal = CollidingMapKey(1)
        val collision = CollidingMapKey(2)
        val custom = linkedMapOf(original to "original", collision to "collision")
        check(custom.put(equal, "replacement") == "original" && custom.size == 2)
        check(custom.keys.first() === original && custom[collision] == "collision")
        check(custom.remove(equal) == "replacement")
        custom[equal] = "reinserted"
        check(custom.keys.toList() == listOf(collision, equal) && custom.keys.last() === equal)
    }
}
