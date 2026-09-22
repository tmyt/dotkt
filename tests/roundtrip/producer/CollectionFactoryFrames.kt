package factoryframes

class FactoryFrameBox<T>(val value: T)
class FactoryFramePair<A, B>(val first: A, val second: B)

class FactoryFrameOwner<T>(private val seed: T) {
    val list: FactoryFrameBox<List<T>> get() = FactoryFrameBox(listOf(seed))
    val many: FactoryFrameBox<List<T>> get() = FactoryFrameBox(listOf(seed, seed))
    val set: FactoryFrameBox<Set<T>> get() = FactoryFrameBox(setOf(seed))
    val map: FactoryFrameBox<Map<String, T>> get() = FactoryFrameBox(mapOf("seed" to seed))

    fun <U> mixed(value: U): FactoryFrameBox<Map<T, U>> = FactoryFrameBox(mapOf(seed to value))
    fun nullable(value: T?): FactoryFrameBox<List<T?>> = FactoryFrameBox(listOf(value))
    fun arrays(values: Array<T>): FactoryFrameBox<List<Array<T>>> = FactoryFrameBox(listOf(values))
    fun functions(callback: (T) -> String): FactoryFrameBox<List<(T) -> String>> = FactoryFrameBox(listOf(callback))
    fun <U> reversed(value: U): FactoryFramePair<List<U>, List<T>> = FactoryFramePair(listOf(value), listOf(seed))
    fun mutable(): FactoryFrameBox<MutableList<T>> = FactoryFrameBox(mutableListOf(seed))
    fun empty(): FactoryFrameBox<List<T>> = FactoryFrameBox(emptyList())
}
