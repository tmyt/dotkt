// bir2cir SuspendColdLowering + InlineSplice (#22) — a `suspend inline fun` with a `crossinline` lambda param
// that is (a) referenced in an ERASED `contract { callsInPlace(block, …) }` (Fir2Ir drops FirContractCallBlock,
// so it never reaches BIR) and (b) invoked INSIDE the `suspendCoroutineUninterceptedOrReturn { … }` intrinsic's
// block — the shape of kotlinx `suspendCancellableCoroutine` and the family over the unintercepted intrinsic.
//   * InlineSplice materializes the crossinline `block` carrier as a `newClosure` the intrinsic block captures.
//   * The cold lowering now cold-transforms the inline suspend WRAPPER's standalone body (app-build gate) AND the
//     non-inline caller whose spliced body holds that materialized closure (a plain closure VALUE is admitted by
//     the shape gate, no longer refused as a lambda kind); the dead intrinsic-block closure class is pruned; a
//     direct `COROUTINE_SUSPENDED` block-return is canonicalized; and the unintercepted form arms its state label
//     BEFORE the block so a SYNCHRONOUS `cont.resume(v)` re-entry lands at the resume point (no unbounded recursion).
import kotlin.coroutines.*
import kotlin.coroutines.intrinsics.*
import kotlin.contracts.*

@OptIn(ExperimentalContracts::class)
suspend inline fun <T> mySuspend(crossinline block: (Continuation<T>) -> Unit): T {
    contract { callsInPlace(block, InvocationKind.EXACTLY_ONCE) }
    return suspendCoroutineUninterceptedOrReturn { uCont ->
        block(uCont)
        COROUTINE_SUSPENDED
    }
}

suspend fun caller(): Int = mySuspend { cont -> cont.resume(5) }

suspend fun other(): Int = mySuspend { cont -> cont.resume(37) }

suspend fun main() {
    // Two synchronous-resume suspensions sequenced in one cold state machine.
    println(caller() + other())
}
