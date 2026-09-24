package roundtrip.projectedlistdefaults

import kotlin.coroutines.*

suspend inline fun listDefault(
    items: List<Any>,
    noinline block: suspend () -> Any? = { items.firstOrNull() },
): Any? = block()

class ListDefaultGate {
    private var pending: Continuation<Unit>? = null
    suspend fun pause() { suspendCoroutine<Unit> { pending = it } }
    suspend fun token(): Int { pause(); return 0 }
    fun release() { pending!!.resume(Unit) }
}

suspend fun delayedListDefault(
    items: List<Any>,
    gate: ListDefaultGate,
    block: suspend () -> Any? = { gate.pause(); items.firstOrNull() },
): Any? = block()

suspend fun listAfterToken(
    items: List<Any>,
    token: Int,
    block: suspend () -> Any? = { items.firstOrNull() },
): Any? = block()

fun nestedLists(items: List<List<Comparable<*>>>): List<List<Comparable<*>>> = items

fun keepComparable(items: List<Comparable<*>>): Any = items
fun mutateComparable(items: MutableList<Comparable<*>>): Int {
    items.add(3)
    return items.size
}
