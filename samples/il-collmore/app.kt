// Common collection ops: mapNotNull, flatMap, flatten, and single-element listOf (element type = T, not Any).
fun main() {
    val xs = listOf(1, 2, 3, 4, 5)
    println(xs.mapNotNull { if (it % 2 == 0) it * 10 else null }.joinToString(","))  // 20,40
    println(xs.flatMap { listOf(it, it * 10) }.joinToString(","))                    // 1,10,2,20,3,30,4,40,5,50

    val nested = listOf(listOf(1, 2), listOf(3), listOf(4, 5))   // listOf(3) is List<Int>, not List<Any>
    println(nested.flatten().joinToString(","))                  // 1,2,3,4,5
    println(nested.flatten().sum())                              // 15

    // a value-returning if/when with a nullable result coerces T/null to Nullable<T>
    fun pick(n: Int): Int? = if (n > 0) n * 2 else null
    println(pick(7) ?: -1)                                       // 14
    println(pick(-1) ?: -1)                                      // -1

    println(xs.average())                                        // 3  (CLR prints 3.0 as 3)
    println(xs.indexOf(4))                                       // 3
}
