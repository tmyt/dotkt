package roundtrip.nullablesuspendvalues

import kotlin.coroutines.*

fun nullableSuspendIdentity(present: Boolean): (suspend (String?) -> String?)? =
    if (present) ({ it }) else null

fun nullableSuspendZero(): (suspend () -> Int)? = { 17 }
fun nullableSuspendTwo(): (suspend (Int, Int) -> Int)? = { a, b -> a + b }

class NullableSuspendGate {
    private var pending: Continuation<Unit>? = null
    var entries = 0
    suspend fun pause() { entries++; suspendCoroutine<Unit> { pending = it } }
    fun release() { val next = pending!!; pending = null; next.resume(Unit) }
    fun callback(): (suspend (String?) -> String?)? = { value -> pause(); value }
}

class NullableSuspendHolder(val callback: (suspend (String?) -> String?)?)

suspend fun checkNullableSuspendValues() {
    check(nullableSuspendIdentity(true)!!(null) == null)
    check(nullableSuspendIdentity(true)!!("value") == "value")
    check(nullableSuspendZero()!!() == 17)
    check(nullableSuspendTwo()!!(19, 23) == 42)
    val local = nullableSuspendIdentity(true)
    if (local != null) check(local("local") == "local")
    check(NullableSuspendHolder(local).callback!!("field") == "field")
    check(nullableSuspendIdentity(false)?.invoke("absent") == null)
    var evaluations = 0
    fun absent(): (suspend (String?) -> String?)? { evaluations++; return null }
    var rejected = false
    try { absent()!!("never") } catch (e: NullPointerException) { rejected = true }
    check(rejected && evaluations == 1)
}
