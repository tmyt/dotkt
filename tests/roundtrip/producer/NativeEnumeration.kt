package roundtrip.enumeration

@Suppress("UNCHECKED_CAST")
fun erasedEnumerationView(value: Any): MutableSet<Any?> = value as MutableSet<Any?>

fun consumeErasedEnumerationView(value: MutableSet<Any?>, element: Any?): Boolean = value.contains(element)

open class ReferencedMutableEnumerationSet<T>(private val storage: MutableList<T>) : AbstractMutableSet<T>() {
    override val size: Int get() = storage.size
    override fun iterator(): MutableIterator<T> = storage.iterator()
    override fun contains(element: T): Boolean = storage.contains(element)
    override fun containsAll(elements: Collection<T>): Boolean = storage.containsAll(elements)
    override fun add(element: T): Boolean = if (storage.contains(element)) false else storage.add(element)
}
