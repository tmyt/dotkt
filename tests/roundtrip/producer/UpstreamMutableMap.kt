package roundtrip.upstreammutablemap

class UpstreamProducedMutableMap<K, V> : AbstractMutableMap<K, V>() {
    private val storage = mutableMapOf<K, V>()
    override val entries: MutableSet<MutableMap.MutableEntry<K, V>> get() = storage.entries
    override fun put(key: K, value: V): V? = storage.put(key, value)
}
fun <T> upstreamProducedMapText(value: T): String = value.toString()
