package roundtrip.iterableidentity

open class ReadOnlyIterable<T>(private val value: T) : Iterable<T> {
    override fun iterator(): Iterator<T> = listOf(value).iterator()
}

class MutableIterableOnly<T>(private val values: MutableList<T>) : MutableIterable<T> {
    override fun iterator(): MutableIterator<T> = values.iterator()
}

class ReadOnlyCollectionWithMutableIteration<T>(private val values: MutableList<T>) :
    Collection<T> by values, MutableIterable<T> {
    override fun iterator(): MutableIterator<T> = values.iterator()
}

inline fun <reified T> iterableIs(value: Any?): Boolean = value is T
inline fun <reified T> iterableSafeCast(value: Any?): T? = value as? T
inline fun <reified T> iterableCheckedCast(value: Any?): T = value as T
inline fun <reified T> capturedIterableIs(): (Any?) -> Boolean = { it is T }

class IterableEvaluation(private val value: Any?) {
    var count: Int = 0
        private set
    fun next(): Any? { count++; return value }
}
