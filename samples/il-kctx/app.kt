// T3 (a) — coroutineContext: the suspend top-level property yields the current coroutine's context (the state
// machine's own Context). The default (no dispatcher/element) is EmptyCoroutineContext.
import clr.Co
import kotlin.coroutines.coroutineContext
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext

suspend fun probe(): Int {
    val ctx: CoroutineContext = coroutineContext
    return if (ctx === EmptyCoroutineContext) 1 else 0   // 1 (default context is empty)
}

fun main() {
    Co.runBlocking {
        println(probe())                                  // 1
        0
    }
}
