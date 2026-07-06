// GAP 1 + GAP 2 (bundle-6 P3 wave-2b) — invoking a suspend functional VALUE `b()` (a param/local/field
// whose type is `suspend (...) -> T`, i.e. a `kotlin.coroutines.SuspendFunctionN`). Unlike a NAMED suspend
// call there is no `<name>$dotkt_suspend` cold entry: the value at runtime is a SuspendLambda state machine,
// driven at the suspension point through the stdlib cold-invoke helper `startSuspendUninterceptedOrReturn`
// (create()+invokeSuspend). Covers: a suspend PARAM value (run1), the higher-order `times`/repeat idiom, a
// suspend value stored in a LOCAL then invoked (local1), and a suspend MEMBER fn that BUILDS a `this`-capturing
// suspend lambda and drives it via a suspend-value call (Box.go — GAP 2). Driven on the cold core by the
// dotkt.support blockOn test harness.
import dotkt.support.blockOn

suspend fun addA(a: Int, b: Int): Int = a + b

suspend fun run1(b: suspend () -> Int): Int = b()                    // invoke a suspend PARAM value

suspend fun times(n: Int, block: suspend () -> Unit) {              // the higher-order repeat idiom
    var i = 0
    while (i < n) { block(); i++ }
}

suspend fun local1(): Int {                                         // a suspend value in a LOCAL, then invoked
    val f: suspend () -> Int = { addA(37, 5) }
    return f()
}

class Box(val n: Int) {
    suspend fun go(): Int = run1 { addA(n, 5) }                     // GAP 2: member builds a this-capturing lambda
}

fun main() {
    println(blockOn { run1 { addA(37, 5) } })   // 42
    var acc = 0
    blockOn { times(3) { acc += 14 } }
    println(acc)                                // 42
    println(blockOn { local1() })               // 42
    println(blockOn { Box(37).go() })           // 42
}
