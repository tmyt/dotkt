package roundtrip.singletonfactories

fun <K, V> singletonInLibrary(key: K, value: V): Map<K, V> = mapOf(key to value)
fun <K, V> singletonCopyInLibrary(source: Map<K, V>): Map<K, V> = source.toMap()
fun <T> singletonSetInLibrary(value: T): Set<T> = setOf(value)
