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
    suspend fun select(): (suspend (String?) -> String?)? { pause(); return callback() }
}

class NullableSuspendHolder(val callback: (suspend (String?) -> String?)?)

class NamedSuspendInvoker(private val gate: NullableSuspendGate) {
    suspend operator fun invoke(value: String): String { gate.pause(); return value }
}

suspend fun checkNullableSuspendValues() {
    check(nullableSuspendIdentity(true)!!(null) == null)
    check(nullableSuspendIdentity(true)!!("value") == "value")
    check(nullableSuspendZero()!!() == 17)
    check(nullableSuspendTwo()!!(19, 23) == 42)
    val local = nullableSuspendIdentity(true)
    if (local != null) check(local("local") == "local")
    check(NullableSuspendHolder(local).callback!!("field") == "field")
    check(nullableSuspendIdentity(false)?.invoke("absent") == null)
    check(nullableSuspendIdentity(true)?.invoke("present") == "present")
    var evaluations = 0
    var arguments = 0
    fun absent(): (suspend (String?) -> String?)? { evaluations++; return null }
    fun argument(): String? { arguments++; return "never" }
    var rejected = false
    try { absent()!!(argument()) } catch (e: NullPointerException) { rejected = true }
    check(rejected && evaluations == 1 && arguments == 0)
}
