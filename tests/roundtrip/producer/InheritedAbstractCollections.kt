package roundtrip.inheritedabstractcollections

interface CollectionTag {
    fun tag(): String
}

open class LibrarySet : AbstractSet<String>(), CollectionTag {
    var emptyCalls = 0
    override val size: Int get() = 1
    override fun iterator(): Iterator<String> = arrayOf("x").iterator()
    override fun isEmpty(): Boolean { emptyCalls++; return size == 0 }
    override fun tag(): String = "set"
}

open class LibraryList<T>(private val elements: List<T>) : AbstractList<T>() {
    override val size: Int get() = elements.size
    override fun get(index: Int): T = elements[index]
}

open class OrdinaryBase<T>(val value: T)
open class LibraryBase : OrdinaryBase<String?>(null), CollectionTag {
    override fun tag(): String = "base"
}
