package roundtrip.indirectcollection

import CollectionFaceInterop.NativeFaces

open class IndirectCollection<T> : CollectionContract<T> {
    override val values: MutableList<T> = mutableListOf()
    override var adds: Int = 0
    var containsCalls = 0
    fun CopyTo(path: String): String = path
    override val size: Int get() = values.size
    override fun isEmpty() = values.isEmpty()
    override fun contains(element: T): Boolean { containsCalls++; return values.contains(element) }
    override fun containsAll(elements: Collection<T>) = values.containsAll(elements)
    override fun iterator() = values.iterator()
    override fun addAll(elements: Collection<T>) = values.addAll(elements)
    override fun remove(element: T) = values.remove(element)
    override fun removeAll(elements: Collection<T>) = values.removeAll(elements)
    override fun retainAll(elements: Collection<T>) = values.retainAll(elements)
    override fun clear() = values.clear()
}

class IndirectCollectionChild<T> : IndirectCollection<T>()

private class CompleteList<T>(elements: MutableList<T>) : ListContract<T>, MutableList<T> by elements
class IndirectList<T> : ListContract<T> by CompleteList<T>(mutableListOf())

fun localIndirectCollection(): Int {
    val source = IndirectCollection<Int>()
    check(source.add(42))
    check(NativeFaces.CopyFirstAndAppend(source as System.Collections.Generic.ICollection<Int>, 42) == 42)
    check(source.size == 2 && source.containsCalls == 1)
    check(source.CopyTo("user overload") == "user overload")
    val child = IndirectCollectionChild<Int>()
    check(child.add(7))
    check(NativeFaces.CopyFirstAndAppend(child as System.Collections.Generic.ICollection<Int>, 7) == 7 && child.adds == 2)
    val list = IndirectList<Int>()
    list.add(10)
    list.add(20)
    check(NativeFaces.IndexOf(list as System.Collections.Generic.IList<Int>, 20) == 1)
    return source.adds
}
