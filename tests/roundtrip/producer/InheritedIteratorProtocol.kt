package roundtrip.iteratorprotocol

open class InheritedIteratorSet<T>(val values: MutableList<T>) : AbstractMutableSet<T>() {
    override val size: Int get() = values.size
    override fun add(element: T): Boolean = if (values.contains(element)) false else values.add(element)
    override fun iterator(): MutableIterator<T> = values.listIterator()
}

abstract class OtherIteratorOwner<Unused, T> : AbstractMutableSet<T>()

fun <T> inheritedIteratorSet(values: MutableList<T>): AbstractMutableSet<T> = InheritedIteratorSet(values)

fun <T> otherIteratorOwner(values: MutableList<T>): OtherIteratorOwner<Int, T> =
    object : OtherIteratorOwner<Int, T>() {
        override val size: Int get() = values.size
        override fun add(element: T): Boolean = if (values.contains(element)) false else values.add(element)
        override fun iterator(): MutableIterator<T> = values.listIterator()
    }
