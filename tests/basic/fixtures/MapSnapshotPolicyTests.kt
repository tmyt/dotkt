import NUnit.Framework.TestAttribute

private fun <K, V> snapshotRawMap(): MutableMap<K, V> = (System.Collections.Hashtable() as Any) as MutableMap<K, V>
private fun <K, V> snapshotKeys(map: Map<K, V>): Set<K> = map.keys
private fun <K, V> snapshotEntries(map: Map<K, V>): Set<Map.Entry<K, V>> = map.entries
private fun <T> snapshotText(value: T): String = value.toString()

class MapSnapshotPolicyTests {
    @TestAttribute
    fun readOnlyKeysShareUpstreamSetPolicyThroughOpaqueAndGenericViews() {
        val mutable = snapshotRawMap<String, Int>()
        mutable["a"] = 1
        val keys = snapshotKeys(mutable)
        check(snapshotText(keys) == "[a]" && (keys as Any).toString() == "[a]")
        check(keys == setOf("a") && setOf("a") == keys)
        check(keys.hashCode() == setOf("a").hashCode())
        val nativeKeys = System.Collections.Generic.HashSet<String>()
        nativeKeys.Add("a")
        check(keys.equals(nativeKeys))
        check(keys.containsAll((nativeKeys as Any) as Set<String>))
        check(!keys.equals(setOf(1)))
        check(!keys.isEmpty() && keys.containsAll(listOf("a")) && !keys.contains("missing"))
        check(keys !is MutableSet<*>)
        mutable["b"] = 2
        check(keys.size == 2 && keys.contains("b"))
        mutable.remove("a")
        check(keys.single() == "b")
    }

    @TestAttribute
    fun capturedEntriesFollowMembershipAndKeepTheirKey() {
        val mutable = snapshotRawMap<Int, String>()
        mutable[1] = "one"
        val entries = snapshotEntries(mutable)
        val entry = entries.single()
        check(snapshotText(entries) == "[1=one]")
        check(entries == mapOf(1 to "one").entries)
        check(entries.hashCode() == mapOf(1 to "one").entries.hashCode())
        check(!entries.equals(setOf("unrelated")) && !entries.equals(setOf<Any?>(null)))
        mutable[1] = "changed"
        mutable[2] = "two"
        check(entries.size == 2 && entry.value == "changed")
        check(entry !is MutableMap.MutableEntry<*, *>)
        mutable.remove(2)
        check(snapshotText(entries) == "[1=changed]")
        check(entries == mapOf(1 to "changed").entries)
        check(entries.hashCode() == mapOf(1 to "changed").entries.hashCode())
    }

    @TestAttribute
    fun emptySnapshotsAndExhaustedIteratorsKeepTheirContracts() {
        val mutable = snapshotRawMap<Int, String>()
        val keys = snapshotKeys(mutable)
        check(keys.isEmpty() && keys.size == 0 && keys.hashCode() == 0)
        check(keys == emptySet<Int>() && snapshotText(keys) == "[]")
        check(keys.containsAll(emptyList()) && !keys.contains(1))
        check(snapshotText(snapshotEntries(mutable)) == "[]")
        val cursor = keys.iterator()
        repeat(2) {
            var rejected = false
            try { cursor.next() } catch (_: NoSuchElementException) { rejected = true }
            check(rejected && !cursor.hasNext())
        }
        mutable[1] = "one"
        check(keys.single() == 1 && !cursor.hasNext())
    }

    @TestAttribute
    fun snapshotsKeepValueAndNullableGenericFrames() {
        val mutable = snapshotRawMap<Int, Int?>()
        mutable[1] = null
        mutable[2] = 0
        val keys = snapshotKeys(mutable)
        check(keys.size == 2 && keys.containsAll(listOf(1, 2)) && !keys.contains(3))
        check(keys == setOf(2, 1) && keys.hashCode() == 3)
        val erasedKeys: Set<Any?> = keys
        check(!erasedKeys.containsAll(listOf("unrelated")))
        var rejectedNull = false
        try { erasedKeys.containsAll(listOf(null)) }
        catch (_: System.ArgumentNullException) { rejectedNull = true }
        check(rejectedNull && !keys.equals(setOf("one", "two")))
        val entries = snapshotEntries(mutable)
        check(entries == mapOf(1 to null, 2 to 0).entries)
        check(entries.hashCode() == mapOf(1 to null, 2 to 0).entries.hashCode())
        mutable[1] = 7
        check(entries.single { it.key == 1 }.value == 7)
        val numbers = snapshotRawMap<Double, Int>()
        numbers[Double.NaN] = 1
        val floating = snapshotKeys(numbers)
        check(floating.contains(Double.NaN) && floating == setOf(Double.NaN))
        val nested = snapshotRawMap<List<Int>, Int>()
        val key = arrayListOf(1, 2)
        nested[key] = 1
        val nestedKeys = snapshotKeys(nested)
        val expected = setOf<List<Int>>(key)
        val equal = arrayListOf(1, 2)
        check(nestedKeys.contains(equal) == nested.containsKey(equal))
        check(nestedKeys == expected && expected == nestedKeys)
        check(nestedKeys.hashCode() == expected.hashCode() && nestedKeys.toString() == "[[1, 2]]")
    }
}
