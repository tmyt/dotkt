import NUnit.Framework.TestAttribute
import DictionaryContracts.GenericOnlyDictionary

private fun <K, V> nativeReadonlyKeys(map: Map<K, V>): Set<K> = map.keys
private fun <K, V> nativeReadonlyEntries(map: Map<K, V>): Set<Map.Entry<K, V>> = map.entries
private fun <T> nativeSnapshotText(value: T): String = value.toString()
private class NativeMapRemapFrame<V> {
    fun <K> merge(map: MutableMap<K, V>, key: K, value: V, remap: (V, V) -> V?): V? = map.merge(key, value, remap)
}

class MapSnapshotPolicyInteropTests {
    @TestAttribute
    fun genericOnlyNativeDictionariesKeepSnapshotsAndLiveEntryValues() {
        val native = GenericOnlyDictionary<Int, String>()
        native.Add(1, "one")
        val readonly = (native as Any) as Map<Int, String>
        val keys = nativeReadonlyKeys(readonly)
        val entries = nativeReadonlyEntries(readonly)
        check(native.EnumeratorsDisposed == 2)
        check(nativeSnapshotText(keys) == "[1]" && keys == setOf(1) && keys.hashCode() == 1)
        check(!keys.equals(setOf("unrelated")) && !keys.equals(setOf<Int?>(null)))
        check(!entries.equals(setOf("unrelated")))
        check(nativeSnapshotText(entries) == "[1=one]" && entries == mapOf(1 to "one").entries)
        val writable = readonly as MutableMap<Int, String>
        writable[1] = "changed"
        native.Add(2, "two")
        check(keys.size == 1 && entries.size == 1 && entries.single().value == "changed")
        check(nativeSnapshotText(entries) == "[1=changed]")
        check(native.EnumeratorsDisposed == 2)
    }

    @TestAttribute
    fun nativeRemapsKeepNullableAndDefaultValuedResultsInCallerFrames() {
        val native = GenericOnlyDictionary<String, Int?>()
        native.Add("key", 42)
        val map = (native as Any) as MutableMap<String, Int?>
        val frame = NativeMapRemapFrame<Int?>()
        check(frame.merge(map, "key", 0) { _, next -> next } == 0)
        check(map.size == 1 && map["key"] == 0)
        check(frame.merge(map, "key", 1) { _, _ -> null } == null)
        check(map.isEmpty())
        check(frame.merge(map, "key", 7) { _, _ -> null } == 7 && map["key"] == 7)
        val flags = (System.Collections.Hashtable() as Any) as MutableMap<Int, Boolean>
        flags[1] = true
        val flagFrame = NativeMapRemapFrame<Boolean>()
        check(flagFrame.merge(flags, 1, false) { _, next -> next } == false)
        check(flags.size == 1 && flags[1] == false)
        check(flagFrame.merge(flags, 1, true) { _, _ -> null } == null && flags.isEmpty())
    }
}
