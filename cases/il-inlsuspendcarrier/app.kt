// BATCH B (#75 holistic) — the SUSPEND CARRIER-VALUE contract for the inline splicer.
// An `inline fun` with a `crossinline` SUSPEND lambda param whose body builds a CAPTURING suspend lambda
// (referencing the crossinline param `t` and the value param `x`) and passes it to a NON-inline fn
// (`dotkt.support.blockOn`, the runBlocking analog). When `wrap` is spliced at the call site the payload
// builds a `newSuspendLambda` that CAPTURES the lambda-param `t` (a non-invoke position) and the value-param
// `x`. Before Batch B this hit the fail-loud guard "payload builds a capturing newSuspendLambda" (InlineSplice
// line 154). Now: MaterializeCarrier's suspend arm mints `t`'s carrier as a real `newSuspendLambda` VALUE, and
// the payload's own `newSuspendLambda` is a hygiene citizen whose `t` capture descriptor is rewritten to that
// materialized temp. The crossinline lambda calls a REAL suspend fn (`addA`) so the body genuinely goes
// through the cold entry — the printed 42 proves the suspend body ran and returned the right value.
import dotkt.support.blockOn

suspend fun addA(a: Int, b: Int): Int = a + b

// non-suspend inline fn, crossinline SUSPEND param, escaping capturing suspend lambda -> non-inline blockOn.
inline fun wrap(x: Int, crossinline t: suspend (Int) -> Int): Int = blockOn { t(x) }

// A second shape: the crossinline suspend lambda captured alongside an enclosing local (`bonus`).
inline fun wrapPlus(x: Int, bonus: Int, crossinline t: suspend (Int) -> Int): Int = blockOn { t(x) + bonus }

fun main() {
    println(wrap(20) { addA(it, 22) })          // 42
    println(wrapPlus(10, 2, { addA(it, 30) }))  // 10+30=40, +2 = 42
    println(wrap(0) { addA(it, 7) })            // 7
}
