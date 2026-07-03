// BUG 2 (bir2cir SuspendColdLowering, eval-order across a suspension): in `left + g()` where g() is a
// suspend call, the LEFT operand must be evaluated (and its side effect observed) BEFORE g() runs — Kotlin
// is strict left-to-right. `side()` prints "L"; the suspend `g()` prints "G". Correct order is L then G.
// Before the fix the left operand was left inline in the returned expression and evaluated AFTER g()'s
// suspension segment -> "G" then "L". Drained by the synthesized plain `main` (g() completes synchronously,
// so the order bug is observable purely through the interleaved println side effects, no genuine async needed).
fun side(): Int { println("L"); return 1 }

suspend fun g(): Int { println("G"); return 2 }

suspend fun f(): Int {
    val r = side() + g()
    return r
}

suspend fun main() {
    println(f())   // expect L, G, 3
}
