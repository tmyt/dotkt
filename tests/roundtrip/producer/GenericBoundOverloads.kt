package roundtrip.genericboundoverloads

import kotlin.coroutines.*

interface Sink<T> { fun send(value: T) }

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
