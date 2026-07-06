// #34a — a suspend LAMBDA that closes over its ENCLOSING instance's members. The `SuspendLambda` SM
// synthesized by bir2cir captures that instance as the `__outer` field; kotc emits the member reads as a
// bare `this.member` (recv `{k:"this"}`) inside the lambda body, which — inside the SM, where `this` is the
// SM itself — must be redirected to read the captured `__outer` field. Before the fix a body `this` leaked
// the SM instance, so `this.n` read garbage. Covered here in every construction position (value / call-arg /
// via a member method / object receiver / nested lambda) plus a local-capture lambda as the non-regression
// control. Driven on the cold Continuation core by the dotkt.support blockOn test harness.
import dotkt.support.blockOn

suspend fun addA(a: Int, b: Int): Int = a + b

class Box(val n: Int) {
    fun makeVal(): suspend () -> Int = { addA(n, 5) }                    // VALUE position: captures this.n
    fun runArg(): Int = blockOn { addA(n, 5) }                          // CALL-ARG position: captures this.n
    fun bump(): Int = n * 2
    fun runMethod(): Int = blockOn { addA(bump(), 1) }                  // this-capture via a member method
    fun outer(): suspend () -> Int = { addA(n, blockOn { addA(n, 0) }) } // NESTED capturing suspend lambda
}

object Holder {
    val base: Int = 100
    fun runObj(): Int = blockOn { addA(base, 5) }                       // object-receiver capture
}

fun mk(k: Int): suspend () -> Int = { addA(k, 5) }                      // LOCAL capture (non-regression control)

fun main() {
    println(blockOn(Box(37).makeVal()))   // 42
    println(Box(37).runArg())             // 42
    println(Box(20).runMethod())          // 41
    println(blockOn(Box(20).outer()))     // 40
    println(Holder.runObj())              // 105
    println(blockOn(mk(37)))              // 42
}
