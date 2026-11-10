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

open class CountingQuerySet<E>(values: List<E>) : QuerySet<E>(values) {
    var calls = 0
    var failure: IllegalStateException? = null
    override fun contains(element: E): Boolean {
        calls++
        val alias = element
        val captured: () -> (() -> Any?) = {
            val nestedAlias = alias
            { nestedAlias }
        }
        check(captured()() == (element as Any?))
        val pending = failure
        if (pending != null) throw pending
        return super.contains(element)
    }
}

open class CountingQueryMap<K, V> : AbstractMap<K, V>() {
    var calls = 0
    var lastKey: Any? = null
    var failure: IllegalStateException? = null
    override val entries: Set<Map.Entry<K, V>> get() = emptySet()
    private fun observe(key: K): Any? {
        val alias = key
        val captured = { alias }
        val pending = failure
        if (pending != null) throw pending
        return captured()
    }
    override fun containsKey(key: K): Boolean { calls++; lastKey = observe(key); return false }
}

interface IntegerQueries : Collection<Int>
interface NullableQueries : Collection<String?>
open class QueryBound
class QueryItem : QueryBound()
class NativeBackedQuerySet<E>(private val values: ArrayList<E>) : AbstractSet<E>() {
    var calls = 0
    override val size: Int get() = values.size
    override fun iterator(): Iterator<E> = values.iterator()
    override fun contains(element: E): Boolean { calls++; return values.contains(element) }
}
interface ItemQueries : Collection<QueryItem>
open class BoundedQuerySet<E : QueryBound>(values: List<E>) : QuerySet<E>(values) {
    var calls = 0
    override fun contains(element: E): Boolean { calls++; return super.contains(element) }
    fun contains(element: String): Boolean = error("unrelated imported overload was selected")
}
