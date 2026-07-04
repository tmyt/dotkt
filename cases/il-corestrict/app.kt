// Coverage (POLISH Wave-2 family 6, item 2): a USER-DEFINED @RestrictsSuspension receiver. The stdlib's
// SequenceScope is @RestrictsSuspension (covered indirectly by il-seq); this exercises the restriction on
// a hand-authored scope, driven by the receiver-form startCoroutine over a plain Continuation completion,
// to confirm it compiles + runs correctly under the cold lowering. @RestrictsSuspension is a FRONTEND
// concern (it forbids calling arbitrary suspend fns inside the block — only the scope's own members) and
// must not perturb bir2cir's cold SM transform: the scope's suspend members lower to ordinary cold entries.
//
// This case pinned a bir2cir bug: a Unit-returning suspend member with NO suspension point (a synchronous
// scope member like `add`) had its DIRECT cold entry fall off the end with no value on the stack (the SM
// branch appends the trailing `return Unit`; the direct branch did not) -> ilverify ReturnMissing / a
// runtime InvalidProgramException. Fixed in SuspendColdLowering.ColdEntryDirect.
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.RestrictsSuspension
import kotlin.coroutines.startCoroutine

@RestrictsSuspension
class Collector<T> {
    val items = ArrayList<T>()
    // A restricted-scope suspend member. It completes synchronously (never returns COROUTINE_SUSPENDED),
    // so the driver's completion continuation runs inline — the sync-completion direct cold-entry path.
    suspend fun add(value: T) { items.add(value) }
    // A scope member calling another scope member across a suspend call inside a for-over-Iterable loop
    // (the control-flow-across-suspension shape; for-over-List desugars to an iterator loop).
    suspend fun addAll(values: List<T>) { for (v in values) add(v) }
}

private class Done : Continuation<Unit> {
    var err: Throwable? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<Unit>) { err = result.exceptionOrNull() }
}

fun <T> collect(block: suspend Collector<T>.() -> Unit): List<T> {
    val c = Collector<T>()
    val d = Done()
    block.startCoroutine(c, d)   // receiver-form startCoroutine drives the restricted scope
    d.err?.let { throw it }
    return c.items
}

fun main() {
    val xs = collect<Int> {
        add(1)
        add(2)
        addAll(listOf(3, 4, 5))
    }
    println(xs.joinToString(","))          // 1,2,3,4,5
    println(xs.size)                        // 5

    val ss = collect<String> {
        add("a")
        add("b")
    }
    println(ss.joinToString("-"))           // a-b
}
