// T7 — Unit as a generic TYPE ARGUMENT: a user `Continuation<Unit>` (Unit can't be System.Void as a generic arg,
// so it erases to the DotKt.Coroutines.Unit singleton). resumeWith(Result<Unit>) + Result.success(Unit).
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext

class UCap : Continuation<Unit> {
    var done: Boolean = false
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<Unit>) { done = result.isSuccess }
}

fun main() {
    val c = UCap()
    c.resumeWith(Result.success(Unit))
    println(c.done)                      // true
}
