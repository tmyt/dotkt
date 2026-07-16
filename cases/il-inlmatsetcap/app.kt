// FIX 2 no-false-positive regression — a MATERIALIZED (§4.4ii, non-suspend newClosure) carrier that MUTATES a
// captured enclosing var must KEEP working. `block` is a crossinline NON-suspend lambda invoked INSIDE a
// suspendCoroutineUninterceptedOrReturn SM (a newSuspendLambda boundary SpliceLambdaInvokes stops at), so it survives
// as a capture and MaterializeCarrier mints it as a real newClosure — the il-suspendnestedcapture path. The carrier
// body does `acc += 10`: because kotc REF-CELL-BOXES a mutated captured var, that write reaches bir2cir as a ref-cell
// FIELD write (not a bare `setLocal` naming the capture), so the materialized closure carries the Ref cell and the
// write-through is visible (prints 10). This pins that FIX 2's setLocal-to-capture refusal (MaterializeCarrier) does
// NOT false-fire on this legitimate ref-cell path — the refusal targets ONLY a bare `setLocal` to a capture, a shape
// kotc's ref-cell boxing keeps off the materialized path; FIX 2 is the loud boundary guard should one ever appear.
import kotlin.coroutines.*
import kotlin.coroutines.intrinsics.*
import dotkt.support.blockOn

suspend inline fun <T> mySuspend(crossinline block: (Continuation<T>) -> Unit): T =
	suspendCoroutineUninterceptedOrReturn { uCont -> block(uCont); COROUTINE_SUSPENDED }

suspend fun capWrite(): Int {
	var acc = 0
	return mySuspend { cont -> acc += 10; cont.resume(acc) }
}

fun main() {
	println(blockOn { capWrite() })   // 10 — ref-cell write-through through the materialized carrier
}
