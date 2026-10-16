package roundtrip.authoredmap

open class ImportedReadOnlyMap<K, V>(protected val storage: MutableMap<K, V>) : Map<K, V> {
    var reads = 0
    override val size: Int get() = storage.size
    override val keys: Set<K> get() = storage.keys
    override val values: Collection<V> get() = storage.values
    override val entries: Set<Map.Entry<K, V>> get() = storage.entries
    override fun isEmpty(): Boolean = storage.isEmpty()
    override fun containsKey(key: K): Boolean = storage.containsKey(key)
    override fun containsValue(value: V): Boolean = storage.containsValue(value)
    override fun get(key: K): V? { reads++; return storage[key] }
}

class ImportedReorderedMap<A, B>(storage: MutableMap<B, A>) : ImportedReadOnlyMap<B, A>(storage)

fun importedMap(): Map<Int, String> = ImportedReorderedMap(mutableMapOf(7 to "seven"))
fun readImportedMap(map: Map<Int, String>): String? = map[7]
