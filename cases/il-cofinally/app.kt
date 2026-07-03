// BUG 1 (bir2cir SuspendColdLowering EmitTry): a suspension INSIDE a try whose finally closes a resource
// (the desugaring of use{}/withLock{}). The state machine returns COROUTINE_SUSPENDED from inside the try,
// so the CLR runs the finally on that `leave` (EARLY) and again on the post-resume normal exit (TWICE) ->
// the resource closes before the awaited value is used and close() prints twice. The fix gates the finally
// on a $suspending flag so it runs EXACTLY ONCE, at the real (post-resume) exit. A genuine `Task.Delay(1)
// .await()` suspension is required (a synchronously-completing suspend call never takes the SUSPENDED
// return, so it would not reproduce). Driven by blockOn.
import System.Threading.Tasks.Task
import kotlin.clr.await
import kotlin.clr.blockOn

class Res {
    fun close() { println("close") }
}

suspend fun useRes(): Int {
    val r = Res()
    try {
        Task.Delay(1).await()   // genuine suspension inside the try body
        return 42
    } finally {
        r.close()               // must print "close" exactly ONCE, AFTER the resume
    }
}

fun main() {
    println(blockOn { useRes() })   // expect: close, 42
}
