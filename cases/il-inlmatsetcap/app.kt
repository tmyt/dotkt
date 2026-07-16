// §4.4ii ref-cell write-through — a MATERIALIZED (non-suspend newClosure) carrier that MUTATES a captured enclosing var
// must write through so the mutation is visible after the inline call. `block` is a crossinline NON-suspend lambda invoked
// INSIDE a suspendCoroutineUninterceptedOrReturn SM (a newSuspendLambda boundary SpliceLambdaInvokes stops at), so it
// survives as a capture and MaterializeCarrier mints it as a real newClosure. The carrier body does `acc += 10`: when kotc
// REF-CELL-BOXES the mutated capture (this path), the write reaches bir2cir as a ref-cell FIELD write and the closure
// carries the Ref cell; when kotc does NOT box (a cross-module inline body's callee-local — the kotlinx.coroutines
// `subscriptionCount` shape), the write reaches MaterializeCarrier as a bare `setLocal` to a capture and bir2cir now
// PROMOTES it to a shared heap cell itself (InlineSplice.BoxMaterializedCaptures) — the same ref-cell machinery, one axis
// over. Either way the enclosing scope sees the write (prints 10).
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
