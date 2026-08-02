// feature fixture — il-inlsuspendnest: a §4.4ii-materialized SUSPEND carrier whose body NESTS a `newSuspendLambda` under a
// multi-scope enclosing tv remap (the real unsafeFlow/combineTransform flow shape). All decls (TYPES and funs) carry
// the `inest`/`NestedGenericSuspendInest` case token so their simple names are UNIQUE across this assembly — a shared type name
// (`Sink`/`Src`) or inline-fn name with the sibling flow case (il-inlsuspendflow) collides in bir2cir's suspend
// member cold-entry naming (keyed by owner-type simple name) at runtime (EntryPointNotFound). Driven by the shared
// `dotkt.support.blockOn` harness; the former `main` + golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

interface NestedGenericSuspendInestSink<T> { suspend fun emit(value: T) }
interface NestedGenericSuspendInestSrc<T> { suspend fun drain(s: NestedGenericSuspendInestSink<T>) }

class NestedGenericSuspendInestListSink<T> : NestedGenericSuspendInestSink<T> {
    val items = ArrayList<T>()
    override suspend fun emit(value: T) { items.add(value) }
}

// a non-inline suspend fn taking a suspend lambda — the lambda literal at the call site becomes a NESTED
// `newSuspendLambda` INSIDE the materialized carrier body (capturing an enclosing local, returning R).
suspend fun <T> nestedGenericSuspendInestProduceOne(block: suspend () -> T): T = block()

inline fun <T> nestedGenericSuspendInestMkFlow(crossinline block: suspend NestedGenericSuspendInestSink<T>.() -> Unit): NestedGenericSuspendInestSrc<T> = object : NestedGenericSuspendInestSrc<T> {
    override suspend fun drain(s: NestedGenericSuspendInestSink<T>) { s.block() }
}

fun <R> nestedGenericSuspendInestMakeSrc(x: R, y: R): NestedGenericSuspendInestSrc<R> = nestedGenericSuspendInestMkFlow<R> {
    emit(nestedGenericSuspendInestProduceOne { x })
    emit(nestedGenericSuspendInestProduceOne { y })
}

suspend fun nestedGenericSuspendInestRunSrc(src: NestedGenericSuspendInestSrc<Int>): Int {
    val sink = NestedGenericSuspendInestListSink<Int>()
    src.drain(sink)
    var sum = 0
    for (x in sink.items) sum += x
    return sum
}

class NestedGenericSuspendLambdaTests {
    @TestAttribute
    fun nestedSuspendLambdaUnderTvRemap() {
        val c = 20; val dd = 22
        val s1 = nestedGenericSuspendInestMkFlow<Int> { emit(nestedGenericSuspendInestProduceOne { c }); emit(nestedGenericSuspendInestProduceOne { dd }) }
        assertEquals(42, blockOn { nestedGenericSuspendInestRunSrc(s1) })                        // 42
        assertEquals(42, blockOn { nestedGenericSuspendInestRunSrc(nestedGenericSuspendInestMakeSrc(30, 12)) })  // 42
    }
}
