// T3(b) — CoroutineContext algebra: the Kotlin members (coroutineContext, plus, fold) map to the .NET algebra
// (EmptyCoroutineContext / Plus / Fold). With no element/dispatcher, fold sees zero elements and plus is identity.
import clr.Co
import kotlin.coroutines.coroutineContext
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext

suspend fun probe(): Int {
    val c: CoroutineContext = coroutineContext            // EmptyCoroutineContext
    val n = c.fold(10) { acc, _ -> acc + 1 }              // 10 (empty -> no elements visited)
    val c2 = c.plus(c)                                     // Empty.plus(Empty) = Empty
    return n + (if (c2 === EmptyCoroutineContext) 5 else 0) // 15
}

fun main() {
    Co.runBlocking {
        println(probe())                                   // 15
        0
    }
}
