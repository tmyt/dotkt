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

fun readComparable(values: System.Collections.Generic.List<Comparable<*>>): Comparable<*> = values[0]
fun compareProjectedString(values: System.Collections.Generic.List<Comparable<in String>>): Int =
    values[0].compareTo("x")
fun readEnum(values: System.Collections.Generic.List<Enum<*>?>): Enum<*>? = values[0]
fun readComparableArray(values: System.Collections.Generic.List<Array<Comparable<*>>>): Array<Comparable<*>> = values[0]
fun invokeComparable(values: System.Collections.Generic.List<() -> Comparable<*>>): Comparable<*> = values[0]()
