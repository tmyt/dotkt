// Kotlin `==` on collections is STRUCTURAL (List ordered, Set unordered, Map entrywise); the CLR-lowered
// BCL collections use reference Object.Equals, so the backend routes a collection == to the stdlib helpers.
fun main() {
    println(listOf(7, 8) == listOf(7, 8))                        // true
    println(listOf(7, 8) == listOf(8, 7))                        // false (order)
    println(setOf(1, 2) == setOf(2, 1))                          // true  (unordered)
    println(mapOf(1 to 2, 3 to 4) == mapOf(3 to 4, 1 to 2))      // true
    println(listOf("a", "b") == listOf("a", "b"))               // true
    println(listOf(1) == setOf(1))                               // false (List vs Set)
    println(listOf(1, 2) != listOf(1, 2))                        // false
    val a = listOf(1, 2, 3)
    println(a == a)                                              // true
    println(mapOf("x" to 1) == mapOf("x" to 2))                 // false (value)
}
