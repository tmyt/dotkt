// #38 — invoking a stored suspend functional VALUE of arity >= 2 (`suspend (A,B,…) -> R`). #36 landed
// arity 0/1 (the fixed create()/create(value) continuation slots); this exercises the GENERAL N-arg cold
// protocol: bir2cir boxes the N invoke args into an Array<Any?> and drives the value through
// `startSuspendUninterceptedOrReturnN(fn, arrayOf(args…), completion)`, which calls the SM's
// `create(args, completion)` override (unpacking args[i] into its param fields) then invokeSuspend. Covers:
// an arity-2 suspend PARAM value (run2), an arity-2 suspend value in a LOCAL (local2), and an arity-3
// CAPTURING lambda (run3 — captures `base`), proving arbitrary N. Driven on the cold core by blockOn.
import dotkt.support.blockOn

suspend fun addA(a: Int, b: Int): Int = a + b

suspend fun run2(b: suspend (Int, Int) -> Int, x: Int, y: Int): Int = b(x, y)   // invoke an arity-2 PARAM value

suspend fun run3(b: suspend (Int, Int, Int) -> Int): Int = b(10, 20, 12)        // arity-3 value invoke

suspend fun local2(): Int {                                                     // an arity-2 value in a LOCAL
    val f: suspend (Int, Int) -> Int = { p, q -> addA(p, q) }
    return f(30, 12)
}

fun main() {
    println(blockOn { run2({ p, q -> addA(p, q) }, 37, 5) })   // 42
    println(blockOn { local2() })                              // 42
    val base = 0
    println(blockOn { run3 { a, b, c -> addA(a, b) + c + base } })   // 42 (captures base)
}
