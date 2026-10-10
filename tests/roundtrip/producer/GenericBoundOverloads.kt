package roundtrip.genericboundoverloads

import kotlin.coroutines.*

interface Sink<T> { fun send(value: T) }
interface Tag

class BoundHost<E : Any> {
    fun <C : MutableCollection<in E>> copy(destination: C): C = destination
    fun <C : Sink<E>> copy(destination: C): C = destination
}

fun <C> copyTagged(destination: C): Int where C : MutableCollection<String>, C : Tag = 1
fun <C> copyTagged(destination: C): Int where C : Sink<String>, C : Tag = 2

class Pending {
    private var completion: Continuation<Unit>? = null
    suspend fun waitHere() { suspendCoroutine<Unit> { completion = it } }
    fun complete() {
        val current = completion ?: error("not suspended")
        completion = null
        current.resume(Unit)
    }
}

class Source<T>(val pending: Pending) {
    var collections = 0
    var sinks = 0
}

fun <E : Any, C : MutableCollection<in E>> Source<E?>.copyTo(destination: C): C {
    collections++
    return destination
}

fun <E : Any, C : Sink<E>> Source<E?>.copyTo(destination: C): C {
    sinks++
    return destination
}

suspend fun <E : Any, C : MutableCollection<in E>> Source<E?>.copyWaiting(destination: C): C {
    pending.waitHere()
    collections++
    return destination
}

suspend fun <E : Any, C : Sink<E>> Source<E?>.copyWaiting(destination: C): C {
    pending.waitHere()
    sinks++
    return destination
}

private class LocalSink<T> : Sink<T> { override fun send(value: T) {} }

fun localCallsKeepTheirBounds(): Boolean {
    val source = Source<String?>(Pending())
    val collection = mutableListOf("local")
    val sink = LocalSink<String>()
    return source.copyTo(collection) === collection && source.copyTo(sink) === sink
        && source.collections == 1 && source.sinks == 1
}

fun <C : System.IComparable> chooseValue(flag: Boolean, value: C): System.IComparable =
    if (flag) value else System.TimeSpan(0, 0, 1)
fun <C : Sink<C>> chooseValue(flag: Boolean, value: C): System.IComparable = System.TimeSpan(0, 0, 2)

fun <C : Comparable<C>> keepComparable(value: C): C = value
fun <C : Sink<C>> keepComparable(value: C): C = value

fun <C : System.IComparable> chooseNullable(value: C?): Int = 1
fun <C : Sink<C>> chooseNullable(value: C?): Int = 2

interface Element
interface Key<E : Element>
class SampleElement : Element
class SampleKey<E : Element> : Key<E>

fun <E : Element, C : MutableCollection<String>> Key<E>.drain(destination: C): Int = 1
fun <E : Element, C : Sink<String>> Key<E>.drain(destination: C): Int = 2

fun <E : Any, C : MutableCollection<in E>> Source<E?>.copyWith(destination: C, callback: (E) -> Unit): C = destination
fun <E : Any, C : Sink<E>> Source<E?>.copyWith(destination: C, callback: (E) -> Unit): C = destination
