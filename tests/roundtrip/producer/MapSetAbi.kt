package roundtrip.mapsetabi

class SetRecorder<K, V> : AbstractMutableMap<K, V>() {
    private val backing = linkedMapOf<K, V>()
    var writes = 0
    override val entries: MutableSet<MutableMap.MutableEntry<K, V>> get() = backing.entries
    override fun put(key: K, value: V): V? { writes++; return backing.put(key, value) }
}

fun <K, V> storeFromLibrary(target: MutableMap<K, V>, key: K, value: V) { target[key] = value }

inline fun <K, V> storeThen(target: MutableMap<K, V>, key: K, value: V, after: (V) -> Unit) {
    target[key] = value
    after(value)
}

class LibrarySetOwner<K> {
    fun <V> store(target: MutableMap<K, V>, key: K, value: V) { target[key] = value }
}
