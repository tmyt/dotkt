// A5 regression gate: a `when (subject)` in EXPRESSION position must evaluate its subject exactly ONCE.
// The old emitter stored the RENDERED subject JSON in valSubst, so every branch test re-spliced — and
// re-evaluated — the subject (f() ran once per branch comparison).
var n = 0

fun f(): Int {
    n++
    return 2
}

fun main() {
    val r = when (f()) {
        1 -> "a"
        2 -> "b"
        else -> "c"
    }
    println(r)
    println(n)   // 1 — not once per branch test
    val s = when (f()) {   // else-hit: still exactly one evaluation
        0 -> "x"
        9 -> "y"
        else -> "z"
    }
    println(s)
    println(n)   // 2
    val i = 7    // stable subject (immutable local): the direct-splice fast path
    val t = when (i) {
        7 -> "seven"
        else -> "other"
    }
    println(t)
}
