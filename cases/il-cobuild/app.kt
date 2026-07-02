// kotlin.clr cold-core coroutine surface: `delay` -> a suspension point (Task.Delay) inside the
// suspend-fun CPS machinery, `blockOn { … }` synchronously drives a suspend block to completion
// (the cold Continuation core's boundary bridge). Pure kotlin.clr — no legacy coroutine stopgap, no DotKt.Runtime.
import kotlin.clr.delay
import kotlin.clr.blockOn

suspend fun compute(n: Int): Int {
    delay(1)              // kotlin.clr.delay -> await Task.Delay
    return n * n
}

suspend fun total(): Int {
    val a = compute(3)    // direct suspend call (await compute's Task)
    val b = compute(4)
    return a + b          // 9 + 16
}

fun main() {
    println(blockOn { total() })   // 25
}
