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

fun main() {
    println(blockOn { recover(5) })     // 10  (mayFail: 5*2)
    println(blockOn { recover(-1) })    // 99  (mayFail throws -> fallback: 100 + (-1))
}
