// #43 — the Batch A × Batch B integration seam. A crossinline SUSPEND carrier (materialized §4.4ii into a real
// newSuspendLambda, exactly like il-inlsuspendcarrier) whose body contains a nested MEMBER-inline call that OMITS a
// lambda-typed default. The inner splice (STEP-8 fixpoint, walked BEFORE the outer materialization) fills that default
// via the #34 member-inline default carriage, re-hoisting a `__dflt$lambda$N` app-local and minting a `newDelegate`
// INSIDE the carrier body. Before the fix, MaterializeSuspendCarrier refused ANY nested `newDelegate` (blanket
// HasNode guard) -> the exact #43 FailLoud. Now the refusal is narrowed to a newDelegate that does NOT resolve
// app-locally (§4.6 cross-module), so the same-module re-hoisted delegate is materialized verbatim. This is the
// `suspendCancellableCoroutineReusable { ... }` family shape that gated the kotlinx.coroutines port.
import dotkt.support.blockOn

suspend fun addA(a: Int, b: Int): Int = a + b

class Chooser(val base: Int) {
	// MEMBER inline fn, a non-const LAMBDA default (`= { -1 }`, a non-capturing lambda -> Tier-2 @KotlinDefault
	// `defaultCarrier` re-hoisted + a `newDelegate`). The BufferedChannel.sendImpl(... onNoWaiterSuspend={...}) shape.
	inline fun pick(cond: Boolean, primary: () -> Int, fallback: () -> Int = { -1 }): Int =
		if (cond) primary() else fallback()
}

// non-suspend inline fn, crossinline SUSPEND param captured into a non-inline blockOn -> §4.4ii suspend materialize.
inline fun wrap(x: Int, crossinline t: suspend (Int) -> Int): Int = blockOn { t(x) }

fun main() {
	val c = Chooser(3)
	// The suspend carrier body nests `c.pick(false, { 5 })` — the omitted `fallback` default is filled with a
	// re-hoisted `__dflt$lambda` newDelegate INSIDE the carrier; cond=false so the default (-1) is used.
	println(wrap(20) { addA(it, c.pick(false, { 5 })) })   // addA(20, -1) = 19
	// override path: fallback NOT taken (cond=true), primary() = 5 -> addA(10, 5) = 15
	println(wrap(10) { addA(it, c.pick(true, { 5 })) })    // 15
	println(wrap(0) { addA(it, c.pick(false, { 5 })) })    // addA(0, -1) = -1
}
