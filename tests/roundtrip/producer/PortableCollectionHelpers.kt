package roundtrip.portablecollections

fun collectionIndices(values: Collection<*>): IntRange = values.indices
fun <T, K> groupedCounts(values: Iterable<T>, keyOf: (T) -> K): Map<K, Int> = values.groupingBy(keyOf).eachCount()
fun <T> fillList(values: MutableList<T>, value: T) { values.fill(value) }
fun <T> shuffleList(values: MutableList<T>) { values.shuffle() }
fun <T> shuffledList(values: Iterable<T>): List<T> = values.shuffled()
fun savedFill(values: MutableList<Int>): (Int) -> Unit = values::fill
fun savedShuffle(values: MutableList<Int>): () -> Unit = values::shuffle
