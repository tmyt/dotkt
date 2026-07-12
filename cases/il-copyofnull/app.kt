// #124: Array<value-type-element>.copyOf(newSize) HONESTLY returns `Array<T?>` — for a value-type T the extra slots
// must be `null` (a `Nullable<T>` with HasValue=false), NOT 0, and the copied prefix must read back correctly. A
// generic-body `arrayOfNulls<T>(newSize)` collapsed to a bare `newarr !T` (an `int[]`) -> value-type garbage / an
// InvalidCast on the `Array<T?>` consumer. copyOf now builds the result by RUNTIME reflection on the receiver's element
// type (a `Nullable<elem>[]` for a value elem, `elem[]` for a reference elem), exact for value AND reference T.
fun main() {
    val a = arrayOf(1, 2, 3)
    println(a.copyOf(5).toList())                 // [1, 2, 3, null, null]  (grow: null tail)
    println(a.copyOf(2).toList())                 // [1, 2]                 (shrink)
    println(a.copyOf(3).toList())                 // [1, 2, 3]              (same size)
    val g = a.copyOf(5)
    println(g[0])                                 // 1     (prefix reads back the real value, not garbage)
    println(g[4])                                 // null  (tail is null, not 0)
    var sum = 0
    for (i in 0 until 3) sum += g[i]!!
    println(sum)                                  // 6

    println(arrayOf(1L, 2L).copyOf(3).toList())   // [1, 2, null]    (Long)
    println(arrayOf(2.5, 3.5).copyOf(3).toList()) // [2.5, 3.5, null] (Double)
    println(arrayOf('a', 'b').copyOf(3).toList()) // [a, b, null]    (Char)

    // reference-type element: null tail, no Nullable wrap.
    println(arrayOf("x", "y").copyOf(3).toList()) // [x, y, null]

    // receiver ALREADY a `Nullable<Int>[]` (guard: no `Nullable<Nullable<Int>>` double-wrap).
    val n = arrayOfNulls<Int>(2)
    n[0] = 7
    println(n.copyOf(3).toList())                 // [7, null, null]
}
