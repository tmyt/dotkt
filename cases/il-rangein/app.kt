// A5 regression gate: range membership `x in a..b` must evaluate x exactly ONCE.
// The old emitter rendered `$x` into BOTH comparison legs of the lowered `(x >= a && x <= b)`,
// so a side-effecting x ran twice.
var c = 0

fun h(): Int {
    c++
    return 5
}

fun main() {
    println(h() in 1..10)     // True
    println(c)                // 1 — not 2
    println(h() in 1 until 5) // False (5 is excluded)
    println(c)                // 2
    val i = 7                 // stable operand: the direct-splice fast path
    println(i in 1..10)       // True
}
