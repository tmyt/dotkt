// N4-sibling (bir2cir SuspendColdLowering, eval-order across a suspension — the ARRAY-ELEMENT case): in
// `a[0] + bump(a)` where `bump(a)` is a suspend call that MUTATES `a[0]`, the LEFT operand `a[0]` must be read
// BEFORE bump() runs (Kotlin is strict left-to-right). An `arrayGet`/`clr.ldelem` element read was mis-classed
// PURE (only the direct FIELD read was fixed by N4), so it was left inline and evaluated AFTER bump()'s
// suspension resumed -> it observed the POST-mutation value (a miscompile). To ISOLATE the array-read case from
// the already-fixed field/getter cases, the array is a plain LOCAL (`a`), not a property (a property getter is a
// `callInstance`, already impure); so the ONLY impure operand on the left is the `arrayGet` itself. bir2cir now
// spills an array-element read that sits left of a suspension into an SM temp (typed precisely from the node's
// `elem` = kotlin.Int, so a value-type element is not boxed to kotlin.Any). Drained by the synthesized plain
// `main` (relay() completes synchronously, so the reorder is observable purely through the element mutation).
suspend fun relay(): Int { return 5 }        // a suspend call that completes synchronously

suspend fun bump(a: IntArray): Int {         // MUTATES a[0], then suspends (so `a[0] + bump(a)` has a suspension on the right)
    a[0] = 100
    return relay()
}

suspend fun compute(): Int {
    val a = intArrayOf(10, 20, 30)
    return a[0] + bump(a)                     // a[0] read LEFT of the suspending bump(a) -> must be 10, not 100
}

suspend fun main() {
    println(compute())                        // expect 10 + 5 = 15 (a miscompile prints 105)
}
