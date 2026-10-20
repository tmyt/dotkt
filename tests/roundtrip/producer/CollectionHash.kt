package roundtrip.collectionhash

fun hashInProducer(value: Any): Int = value.hashCode()
fun <T> genericHashInProducer(value: T): Int = value?.hashCode() ?: 0
inline fun <T> inlineHashInProducer(value: T): Int = value?.hashCode() ?: 0
fun producerHashReader(value: Any): () -> Int = value::hashCode
fun producerHashList(): Any = listOf(23, 42)
fun producerHashMap(): Any = mapOf(1 to "a", 2 to "b")
fun producerHashSet(): Any = setOf(23, 42)
fun <T> producerKeyMap(key: T): HashMap<T, String> = hashMapOf(key to "stored")
fun <T> producerKeySet(key: T): HashSet<T> = hashSetOf(key)
