// User class implementing Kotlin's Comparator<T> -> a CLR class : IComparer<T> (compare -> Compare).
class IntCmp : Comparator<Int> {
    override fun compare(a: Int, b: Int): Int = a - b
}
fun main() {
    val c = IntCmp()
    println(c.compare(2, 5))   // -3
    println(c.compare(9, 4))   // 5
    println(c.compare(3, 3))   // 0
}
