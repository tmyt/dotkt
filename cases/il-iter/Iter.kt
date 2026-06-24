// User-defined iterator operator: `for (x in obj)` over a user type.
class IntBoxIterator(val items: IntArray) {
    var idx = 0
    operator fun hasNext(): Boolean = idx < items.size
    operator fun next(): Int {
        val v = items[idx]
        idx = idx + 1
        return v
    }
}

class IntBox(val items: IntArray) {
    operator fun iterator(): IntBoxIterator = IntBoxIterator(items)
}

// Idiom 2: iterator() returning an anonymous object implementing kotlin.collections.Iterator<T>.
class Countdown(val from: Int) {
    operator fun iterator(): Iterator<Int> = object : Iterator<Int> {
        var cur = from
        override fun hasNext(): Boolean = cur > 0
        override fun next(): Int {
            val v = cur
            cur = cur - 1
            return v
        }
    }
}

fun main() {
    val box = IntBox(intArrayOf(10, 20, 30))
    var sum = 0
    for (x in box) {
        println("x=$x")
        sum = sum + x
    }
    println("sum = $sum")

    var acc = 0
    for (n in Countdown(3)) {
        println("n=$n")
        acc = acc + n
    }
    println("acc = $acc")
}
