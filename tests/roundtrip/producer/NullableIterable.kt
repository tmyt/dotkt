package roundtrip.nullableiterable

fun <T> countPresent(ignored: Int, items: Iterable<T?>): Int {
    var count = 0
    for (item in items) if (item != null) count++
    return count
}

open class Bag<A, T>(val items: List<T>) : Iterable<T> {
    override fun iterator(): Iterator<T> = items.iterator()
}

class DerivedBag<T>(items: List<T>) : Bag<String, T>(items)

fun <T> countViaOpenElement(items: Iterable<T>): Int = countPresent<T>(0, items)
