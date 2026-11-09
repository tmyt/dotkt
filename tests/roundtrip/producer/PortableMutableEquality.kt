package roundtrip.mutablepolicy

fun <T> portableReadOnlyBase(value: AbstractMutableCollection<T>): AbstractCollection<T> = value
fun <T> portableMutableBase(value: AbstractMutableList<T>): AbstractMutableCollection<T> = value
fun <T> portableMutableBase(value: AbstractMutableSet<T>): AbstractMutableCollection<T> = value

open class PortableWideValue<T>(val element: T)
class PortableNarrowValue<T>(element: T) : PortableWideValue<T>(element)
abstract class PortableWideSelection<T> { abstract fun selected(): PortableWideValue<T> }
interface PortableNarrowSelection<T> { fun selected(): PortableNarrowValue<T> }
abstract class PortableInheritedSelection<T> : PortableWideSelection<T>(), PortableNarrowSelection<T> {
    fun readSelected(): PortableNarrowValue<T> = selected()
}
class PortableSelected<T>(val value: PortableNarrowValue<T>) : PortableInheritedSelection<T>() {
    override fun selected(): PortableNarrowValue<T> = value
}

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
