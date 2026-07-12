// #120: Array<value-type-element>.plus / .plusElement must preserve the receiver's reified element type. The
// pure-Kotlin body `val result = arrayOfNulls<T>(size+1); result[i]=this[i]; result as Array<T>` had its body-local
// `var result: Array<T?>` slot object-erased (`object[]`) while the allocation stayed `newarr !T` and the stelem
// was `object` -> value-type slots read back as garbage. #120 gates the body-local reified-array element back to bare
// `!T`, so var-slot / newarr / stelem / ldelem all agree. Value AND reference T asserted (reference already worked).
fun main() {
    val a = arrayOf(1, 2, 3)
    println(a.plus(4).toList())          // [1, 2, 3, 4]
    println(a.plusElement(5).toList())   // [1, 2, 3, 5]
    println(a.sum())                     // 6  (receiver untouched, real int[])

    // General across other value-type elements.
    val la = arrayOf(1L, 2L, 3L)
    println(la.plus(4L).toList())         // [1, 2, 3, 4]
    val da = arrayOf(1.5, 2.5)
    println(da.plusElement(3.5).toList()) // [1.5, 2.5, 3.5]
    val ca = arrayOf('a', 'b')
    println(ca.plus('c').toList())        // [a, b, c]

    // Reference-element arrays stay correct.
    val s = arrayOf("a", "b")
    println(s.plus("c").toList())         // [a, b, c]
    println(s.plusElement("d").toList())  // [a, b, d]
}
