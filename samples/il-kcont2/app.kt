// T5 — a USER Kotlin class implements kotlin.coroutines.Continuation<Int> (interface whose resumeWith takes the
// generic STRUCT param Result<Int>). Proves user Continuation impls compile (the §13j gap): kotlin.Result maps to
// the shared DotKt.Coroutines.Result, the Kotlin members resumeWith/context bind to the .NET ResumeWith/Context.
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext

class Capture : Continuation<Int> {
    var got: Int = 0
    var err: String? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<Int>) {
        if (result.isSuccess) got = result.getOrThrow()
        else err = result.exceptionOrNull()?.message
    }
}

fun main() {
    val c = Capture()
    c.resumeWith(Result.success(42))
    println(c.got)                                  // 42

    val c2 = Capture()
    c2.resumeWith(Result.failure(RuntimeException("boom")))
    println(c2.err)                                 // boom
}
