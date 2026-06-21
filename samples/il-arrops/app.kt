// Collection ops on Arrays (LINQ over IEnumerable<T>) + value-type firstOrNull/lastOrNull returning a real
// nullable (Kotlin's null for empty, not LINQ's default(0)).
fun main() {
    val xs = arrayOf(3, 1, 4, 1, 5)
    println(xs.firstOrNull() ?: -1)                                 // 3
    println(xs.map { it * 2 }.filter { it > 4 }.joinToString(","))  // 6,8,10
    println(xs.sum())                                              // 14
    println(xs.count { it == 1 })                                  // 2
    println((arrayOf<Int>()).firstOrNull() ?: -1)                  // -1

    val v = listOf(10, 20, 30)
    println(v.firstOrNull() ?: -1)                                 // 10  (value-type firstOrNull fix)
    println(v.lastOrNull() ?: -1)                                  // 30
}
