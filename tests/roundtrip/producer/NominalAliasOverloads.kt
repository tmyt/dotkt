package roundtrip.nominalaliasoverloads

import kotlin.coroutines.*
import kotlin.clr.ClrName

fun route(value: Iterable<Int>): Int = 11
fun route(value: Sequence<Int>): Int = 22

@ClrName("sharedNominalRoute")
fun namedIterable(value: Iterable<Int>): Int = 71
@ClrName("sharedNominalRoute")
fun namedSequence(value: Sequence<Int>): Int = 82

inline fun invokeAliasBlock(block: () -> Int): Int = block()

fun <T> Iterable<T>.keepNominal(): Iterable<T> = this
fun <T> Sequence<T>.keepNominal(): Sequence<T> = this

class AliasHost {
    fun route(value: Iterable<Int>): Int = 31
    fun route(value: Sequence<Int>): Int = 42
}

fun localAliasCalls(): Boolean {
    val values: Iterable<Int> = listOf(1)
    val sequence = sequenceOf(2)
    return route(values) == 11 && route(sequence) == 22
        && values.keepNominal() === values && sequence.keepNominal() === sequence
}

fun <T> forwardSequence(value: Sequence<T>): Sequence<T> = value.keepNominal()

class Pause {
    private var continuation: Continuation<Unit>? = null
    suspend fun waitHere() {
        suspendCoroutine<Unit> { continuation = it }
    }
    fun complete() {
        val current = continuation ?: error("not suspended")
        continuation = null
        current.resume(Unit)
    }
}

suspend fun <T> Iterable<T>.routeWaiting(pause: Pause): String {
    pause.waitHere()
    return "iterable"
}

suspend fun <T> Sequence<T>.routeWaiting(pause: Pause): String {
    pause.waitHere()
    return "sequence"
}
