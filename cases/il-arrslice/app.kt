// #117: Array<value-type-element>.slice/take/takeLast (all route through copyOfRange) must preserve the receiver's
// runtime element type. A pure-Kotlin `arrayOfNulls<T>(n) as Array<T>` allocated Nullable<int>[]/object[] and
// reinterpret-cast it to int[] -> garbage. copyOfRange is now a runtime-element-type-preserving native intrinsic
// (System.Array.CreateInstanceFromArrayType(this.GetType(), len) + System.Array.Copy), exact for value AND reference T.
fun main() {
    val a = arrayOf(10, 20, 30, 40, 50)
    println(a.slice(1..3))          // [20, 30, 40]
    println(a.take(2))              // [10, 20]
    println(a.takeLast(2))          // [40, 50]
    val r = a.copyOfRange(1, 4)     // direct copyOfRange
    println(r.size)                 // 3
    println(r[0])                   // 20
    println(r.sum())                // 90  (real int[] arithmetic, not garbage)

    // General across other value-type elements.
    val la = arrayOf(1L, 2L, 3L, 4L)
    println(la.take(2))             // [1, 2]
    val da = arrayOf(1.5, 2.5, 3.5)
    println(da.takeLast(2))         // [2.5, 3.5]
    val ca = arrayOf('a', 'b', 'c', 'd')
    println(ca.slice(1..2))         // [b, c]

    // Reference-element arrays stay correct.
    val s = arrayOf("a", "b", "c", "d")
    println(s.slice(1..2))          // [b, c]
    println(s.take(2))              // [a, b]
    println(s.takeLast(2))          // [c, d]
}
