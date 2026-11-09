package roundtrip.erasedqueries

open class QuerySet<E>(private val values: List<E>) : AbstractSet<E>() {
    override val size: Int get() = values.size
    override fun iterator(): Iterator<E> = values.iterator()
    fun matchesAll(elements: Collection<E>): Boolean {
        for (element in elements) {
            val alias = element
            if (!contains(alias)) return false
        }
        return true
    }
}
