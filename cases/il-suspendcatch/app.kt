// #78 Defect B (catch-hoist) — a suspend call INSIDE a catch handler. Resuming into a CLR catch clause is illegal
// IL, so bir2cir's HoistSuspendingCatches lifts the handler out of the clause: the real catch only records the
// exception into an SM-field-backed capture, and the handler body (`fallback(x)`, itself a suspension) runs as
// gated straight-line code after the try, where the state machine segments it. This is the kotlinx.coroutines
// `SelectImplementation.processResultAndInvokeBlockRecoveringException` shape (Select.kt:723). The try BODY also
// suspends (`mayFail`), so this exercises the two-level suspending-try dispatch AND the hoisted suspending catch.
import System.Threading.Tasks.Task
import kotlin.clr.await
import dotkt.support.blockOn

suspend fun mayFail(x: Int): Int {
    Task.Delay(1).await()          // genuine suspension in the TRY body
    if (x < 0) throw IllegalStateException("neg")
    return x * 2
}

suspend fun fallback(x: Int): Int {
    Task.Delay(1).await()          // genuine suspension in the CATCH handler
    return 100 + x
}

suspend fun recover(x: Int): Int =
    try {
        mayFail(x)
    } catch (e: IllegalStateException) {
        fallback(x)
    }

// MULTI-CATCH — two catch clauses, BOTH handlers suspend (each hoisted with its own capture). The try body also
// throws distinct types + suspends, exercising the per-clause record + gated-dispatch ordering.
suspend fun classify(x: Int): Int =
    try {
        if (x == 1) throw IllegalStateException("a")
        if (x == 2) throw IllegalArgumentException("b")
        fallback(x)                       // suspends; returns 100 + x
    } catch (e: IllegalStateException) {
        fallback(100)                     // suspend handler 1 -> 200
    } catch (e: IllegalArgumentException) {
        fallback(200)                     // suspend handler 2 -> 300
    }

fun main() {
    println(blockOn { recover(5) })      // 10  (mayFail: 5*2)
    println(blockOn { recover(-1) })     // 99  (mayFail throws -> fallback: 100 + (-1))
    println(blockOn { classify(3) })     // 103 (no throw -> fallback: 100 + 3)
    println(blockOn { classify(1) })     // 200 (ISE -> fallback(100))
    println(blockOn { classify(2) })     // 300 (IAE -> fallback(200))
}
