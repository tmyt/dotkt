interface RequiredRemoval<K, V> { fun remove(key: K, value: V): Boolean }

abstract class MixedMap : AbstractMutableMap<String, Int>(), RequiredRemoval<String, Int> {
    fun remove(key: String, value: Int): Boolean = false
}
