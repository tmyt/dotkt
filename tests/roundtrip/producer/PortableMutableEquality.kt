package roundtrip.mutablepolicy

class PortableMutableList<T>(private val values: MutableList<T>) : AbstractMutableList<T>() {
    var iterations: Int = 0
    override val size: Int get() = values.size
    override fun get(index: Int): T = values[index]
    override fun set(index: Int, element: T): T = values.set(index, element)
    override fun add(index: Int, element: T) { values.add(index, element); modCount++ }
    override fun removeAt(index: Int): T {
        val previous = values.removeAt(index)
        modCount++
        return previous
    }
    override fun iterator(): MutableIterator<T> { iterations++; return super.iterator() }
}

class PortableMutableSet<T>(private val values: MutableList<T>) : AbstractMutableSet<T>() {
    var iterations: Int = 0
    override val size: Int get() = values.size
    override fun add(element: T): Boolean = if (element in values) false else values.add(element)
    override fun iterator(): MutableIterator<T> { iterations++; return values.listIterator() }
}

class PortableMutableKey(val value: Int) {
    override fun equals(other: Any?): Boolean = other is PortableMutableKey && value == other.value
    override fun hashCode(): Int = 17 * value
}

fun portableMutableEquals(value: Any, other: Any?): Boolean = value.equals(other)
fun portableMutableHash(value: Any): Int = value.hashCode()
