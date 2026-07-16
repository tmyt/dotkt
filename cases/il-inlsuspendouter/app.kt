// BATCH B (#75 holistic, 2B) — the `__outer` receiver rebind (the 52nd site). An EXTENSION `inline fun T.op`
// whose body builds a SOURCE `newSuspendLambda` (the `blockOn { … }` arg) that captures `this@op` — the
// enclosing extension receiver, kotc's `__outer`. Before 2B, SuspendCaptureHazard(refuseOuter:true) refused it:
// SuspendLambdaLowering's fallback would bind `__outer`'s construction value to the CALLER's `this`/`__self`,
// not op's extension receiver. 2B rebinds each payload-frame `newSuspendLambda`'s `__outer` construction value
// (via a `capValues` override) to the splice's bound receiver temp (`<prefix>__self` for an extension). The
// crossinline SUSPEND `f` is ALSO captured by that same suspend lambda and materialized (§4.4ii). Drives
// end-to-end via blockOn; the value MUST be correct.
import dotkt.support.blockOn

suspend fun addA(a: Int, b: Int): Int = a + b

// extension inline fun; the payload's `blockOn { … }` lambda (a SOURCE newSuspendLambda) captures `this@op`
// (= __outer, the extension receiver) alongside the crossinline suspend carrier `f`.
inline fun <T> T.op(crossinline f: suspend (T) -> Int): Int = blockOn { f(this@op) }

fun main() {
    println(20.op { addA(it, 22) })   // f(20) = addA(20, 22) = 42
    println(0.op { addA(it, 7) })     // f(0)  = addA(0, 7)   = 7
}
