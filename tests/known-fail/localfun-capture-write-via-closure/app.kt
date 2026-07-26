// KNOWN FAILURE — fails at BIR2CIR (the build aborts; no IL is produced):
//   bir2cir: AppKt.__lambda0: sanity: 'local' references undeclared local 'n' (no matching var/param in scope)
//
// A lambda that CALLS a capturing local `fun` does not itself capture what that local fun captures. kotc lifts the
// local fun to a static method taking its captures as leading by-value parameters (BirEmitterLifts.liftLocalFn), and
// the call site supplies those values (BirEmitterCalls, via `localFns`) — but `capturedVars` does not follow an
// `IrCall` into the lifted method, so the enclosing `n` is not in the lambda's own capture set. The `callStatic`
// emitted inside the lambda's `invoke` then passes `{k:local,name:"n"}`, which exists only in the declaring frame.
//
// Independent of heap ref-cells: it fails the same way whether or not `n` is promoted to a cell (here nothing writes
// `n` across a class/lambda boundary, so nothing promotes it). This is the second of the two blockers to making a
// local fun a ref-cell capture boundary — see tests/known-fail/localfun-capture-write/ for the first and for the
// silent-wrong-value defect itself.

fun closureCallsLocalFun(): Int {
    var n = 0
    fun bump() { n++ }
    val g = { bump() }
    g(); g()
    return n                      // expected 2
}

fun main() {
    println(closureCallsLocalFun())
}
