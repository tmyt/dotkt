// User class implementing Kotlin's Comparable<V> (self-referential generic) -> a CLR class : IComparable<V>
// (compareTo -> CompareTo). The `<`/`>`/`<=`/`>=` operators desugar to compareTo; `sorted()` uses it.
class Ver(val major: Int, val minor: Int) : Comparable<Ver> {
    override fun compareTo(o: Ver): Int = if (major != o.major) major - o.major else minor - o.minor
    override fun toString(): String = "" + major + "." + minor
}
fun main() {
    val a = Ver(1, 2); val b = Ver(1, 5); val c = Ver(2, 0)
    println(if (a < b) "a<b" else "a>=b")        // a<b
    println(if (c > b) "c>b" else "c<=b")        // c>b
    println(if (a <= a) "a<=a" else "no")        // a<=a
    println(a.compareTo(b))                       // -3
    val sorted = listOf(c, a, b).sorted()         // uses compareTo
    println(sorted.joinToString(","))             // 1.2,1.5,2.0
}
