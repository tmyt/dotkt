import NUnit.Framework.TestAttribute
import roundtrip.mapsnapshot.*

private fun <K, V> localReadonlySnapshotEntries(map: Map<K, V>): Set<Map.Entry<K, V>> = map.entries

class CrossModuleMapSnapshotPolicyTests {
    @TestAttribute
    fun snapshotsRetainInheritedSetPolicyAcrossDllBoundaries() {
        val map = rawSnapshotMap<String, Int>()
        map["a"] = 1
        val keys = importedSnapshotKeys(map)
        check(importedSnapshotText(keys) == "[a]" && (keys as Any).toString() == "[a]")
        check(keys == setOf("a") && keys.hashCode() == setOf("a").hashCode())
        check(!keys.equals(setOf(1)) && !keys.equals(setOf<String?>(null)))
        val entries = localReadonlySnapshotEntries(map)
        check(!entries.equals(setOf("unrelated")))
        check(importedSnapshotText(entries) == "[a=1]" && entries == mapOf("a" to 1).entries)
        map["a"] = 2
        map["b"] = 3
        check(keys.size == 1 && entries.size == 1 && entries.single().value == 2)
        check(importedSnapshotText(entries) == "[a=2]")
    }

    @TestAttribute
    fun snapshotsRetainNullableValueFramesAndIteratorProtocolAcrossDlls() {
        val map = rawSnapshotMap<Int, Int?>()
        map[1] = null
        val entries = localReadonlySnapshotEntries(map)
        check(importedSnapshotText(entries) == "[1=null]")
        check(entries == mapOf(1 to null).entries)
        map[1] = 0
        check(entries.single().value == 0 && importedSnapshotText(entries) == "[1=0]")
        val cursor = importedSnapshotKeys(map).iterator()
        check(cursor.next() == 1 && !cursor.hasNext())
        repeat(2) {
            var rejected = false
            try { cursor.next() } catch (_: NoSuchElementException) { rejected = true }
            check(rejected && !cursor.hasNext())
        }
    }
}
