package roundtrip.genericindexing

open class Index {
    var last: Any? = null
    open operator fun <T> get(value: T): T = value
    open operator fun <T> set(index: Int, value: T) { last = value }
    operator fun get(value: Int, suffix: String): String = "$value:$suffix"
}

class DerivedIndex : Index()

class OwnerIndex<O>(val owner: O) {
    var last: Any? = null
    operator fun <T> get(value: T): T = value
    operator fun <T> set(key: O, value: T) {
        check(key == owner)
        last = value
    }
}

open class Element(val id: Int)
class Key<E : Element>(val value: E)
class Lookup {
    var last: Element? = null
    operator fun <E : Element> get(key: Key<E>): E? = key.value
    operator fun <E : Element> set(key: Key<E>, value: E?) { last = value }
}

class PlainIndex {
    var last: Int = 0
    operator fun get(index: Int): Int = index + 1
    operator fun set(index: Int, value: Int) { last = index + value }
}
