package roundtrip.iteratorprotocol

open class InheritedIteratorSet<T>(val values: MutableList<T>) : AbstractMutableSet<T>() {
    override val size: Int get() = values.size
    override fun add(element: T): Boolean = if (values.contains(element)) false else values.add(element)
    override fun iterator(): MutableIterator<T> = values.listIterator()
}

abstract class OtherIteratorOwner<Unused, T> : AbstractMutableSet<T>()

abstract class ReadonlyIteratorOwner<Unused, T> : Iterable<T>

open class IteratorProperty<T>(val iterator: Iterator<T>)
open class MutableIteratorProperty<T>(val iterator: MutableIterator<T>)

class RenamedMutableIteratorMethod(val iterator: Iterator<Int>) {
    var calls = 0
    @kotlin.clr.ClrName("CreateIterator")
    fun iterator(): MutableIterator<Int> { calls++; return mutableListOf(7).iterator() }
}

class RenamedReadonlyIteratorMethod(val iterator: MutableIterator<Int>) {
    var calls = 0
    @kotlin.clr.ClrName("ReadIterator")
    fun iterator(): Iterator<Int> { calls++; return listOf(9).iterator() }
}

fun <T> inheritedIteratorSet(values: MutableList<T>): AbstractMutableSet<T> = InheritedIteratorSet(values)

fun <T> readonlyIteratorOwner(values: List<T>): ReadonlyIteratorOwner<Int, T> =
    object : ReadonlyIteratorOwner<Int, T>() {
        override fun iterator(): Iterator<T> = values.iterator()
    }

fun <T> otherIteratorOwner(values: MutableList<T>): OtherIteratorOwner<Int, T> =
    object : OtherIteratorOwner<Int, T>() {
        override val size: Int get() = values.size
        override fun add(element: T): Boolean = if (values.contains(element)) false else values.add(element)
        override fun iterator(): MutableIterator<T> = values.listIterator()
    }
