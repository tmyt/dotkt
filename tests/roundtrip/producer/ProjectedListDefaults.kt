package roundtrip.projectedlistdefaults

import kotlin.coroutines.*

suspend inline fun listDefault(
    items: List<Any>,
    noinline block: suspend () -> Any? = { items.firstOrNull() },
): Any? = block()

class ListDefaultGate {
    private var pending: Continuation<Unit>? = null
    suspend fun pause() { suspendCoroutine<Unit> { pending = it } }
    fun release() { pending!!.resume(Unit) }
}

suspend fun delayedListDefault(
    items: List<Any>,
    gate: ListDefaultGate,
    block: suspend () -> Any? = { gate.pause(); items.firstOrNull() },
): Any? = block()

fun nestedLists(items: List<List<Comparable<*>>>): List<List<Comparable<*>>> = items
