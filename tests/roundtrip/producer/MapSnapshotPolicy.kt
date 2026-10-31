package roundtrip.mapsnapshot

fun <K, V> rawSnapshotMap(): MutableMap<K, V> = (System.Collections.Hashtable() as Any) as MutableMap<K, V>
fun <K, V> importedSnapshotKeys(map: Map<K, V>): Set<K> = map.keys
fun <T> importedSnapshotText(value: T): String = value.toString()
