// Bounded type parameters: <T : Comparable<T>> — needs a CLR generic constraint (IComparable<T>)
// so `a > b` (= a.compareTo(b) > 0) is callable on a bare T.
fun <T : Comparable<T>> maxOf2(a: T, b: T): T = if (a > b) a else b

class SortedPair<T : Comparable<T>>(val a: T, val b: T) {
    fun larger(): T = if (a > b) a else b
}

fun main() {
    println(maxOf2(3, 7))               // 7
    println(maxOf2("apple", "banana"))  // banana
    val p = SortedPair(10, 4)
    println(p.larger())                 // 10
}
