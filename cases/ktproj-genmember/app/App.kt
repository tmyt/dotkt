import p2.Pair2
import p2.Wrap
import p2.pair2
import p2.wrap

fun main() {
    val p: Pair2<Int, MutableList<Int>> = pair2(7, mutableListOf(1, 2))
    // a DIRECT read of a cross-module generic member whose declared return is the OWNER's type variable. Surface
    // substitutes tv(type,0)->Int (non-collection, index 0) and tv(type,1)->MutableList<Int> (collection, index 1).
    println(p.a)      // 7
    println(p.b)      // [1, 2]
    // a member whose declared return NESTS the type variable (`List<X>`): the recursive substitution rewrites
    // List<tv(type,0)> -> List<Int>, so the read Kotlin-formats instead of printing the raw BCL List.
    val w: Wrap<Int> = wrap(listOf(9, 8, 7))
    println(w.items)  // [9, 8, 7]
}
