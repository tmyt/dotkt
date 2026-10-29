package roundtrip.portablecollections

fun collectionIndices(values: Collection<*>): IntRange = values.indices
fun <T, K> groupedCounts(values: Iterable<T>, keyOf: (T) -> K): Map<K, Int> = values.groupingBy(keyOf).eachCount()
fun <T> fillList(values: MutableList<T>, value: T) { values.fill(value) }
fun <T> shuffleList(values: MutableList<T>) { values.shuffle() }
fun <T> shuffledList(values: Iterable<T>): List<T> = values.shuffled()
fun savedFill(values: MutableList<Int>): (Int) -> Unit = values::fill
fun savedShuffle(values: MutableList<Int>): () -> Unit = values::shuffle
fun <T> exchangeFirstTwo(values: MutableList<T>) {
    val first = values[0]
    values[0] = values[1]
    values[1] = first
}
fun <T> mutableAliasSize(values: MutableCollection<T>): Int = values.size
fun <K, V> mutableMapAliasSize(values: MutableMap<K, V>): Int = values.size
