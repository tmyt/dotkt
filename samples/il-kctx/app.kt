// T3(b) — CoroutineContext element algebra with a REAL element: put an IntTag into the context, get it back by key.
import clr.Co
import clr.IntTag
import clr.Tags
import kotlin.coroutines.coroutineContext
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext

suspend fun probe(): Int {
    val empty: CoroutineContext = coroutineContext            // EmptyCoroutineContext
    val n = empty.fold(10) { acc, _ -> acc + 1 }              // 10 (no elements)

    val ctx = empty.plus(IntTag(42))                          // Empty + IntTag(42)
    val got = ctx.get(Tags.tagKey())                          // get by key -> IntTag(42)
    val v = if (got != null) got.value else -1                // 42

    val back = ctx.minusKey(Tags.tagKey())                    // remove -> Empty
    val emptyAgain = if (back === EmptyCoroutineContext) 1 else 0  // 1

    return n + v + emptyAgain                                  // 10 + 42 + 1 = 53
}

fun main() {
    Co.runBlocking {
        println(probe())                                       // 53
        0
    }
}
