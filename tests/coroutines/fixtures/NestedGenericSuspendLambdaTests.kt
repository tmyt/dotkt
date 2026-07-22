// CorB batch — il-inlsuspendnest: a §4.4ii-materialized SUSPEND carrier whose body NESTS a `newSuspendLambda` under a
// multi-scope enclosing tv remap (the real unsafeFlow/combineTransform flow shape). All decls (TYPES and funs) carry
// the `inest`/`CorBInest` case token so their simple names are UNIQUE across this assembly — a shared type name
// (`Sink`/`Src`) or inline-fn name with the sibling flow case (il-inlsuspendflow) collides in bir2cir's suspend
// member cold-entry naming (keyed by owner-type simple name) at runtime (EntryPointNotFound). Driven by the shared
// `dotkt.support.blockOn` harness; the former `main` + golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

interface CorBInestSink<T> { suspend fun emit(value: T) }
interface CorBInestSrc<T> { suspend fun drain(s: CorBInestSink<T>) }

class CorBInestListSink<T> : CorBInestSink<T> {
    val items = ArrayList<T>()
    override suspend fun emit(value: T) { items.add(value) }
}

// a non-inline suspend fn taking a suspend lambda — the lambda literal at the call site becomes a NESTED
// `newSuspendLambda` INSIDE the materialized carrier body (capturing an enclosing local, returning R).
suspend fun <T> corBInestProduceOne(block: suspend () -> T): T = block()

inline fun <T> corBInestMkFlow(crossinline block: suspend CorBInestSink<T>.() -> Unit): CorBInestSrc<T> = object : CorBInestSrc<T> {
    override suspend fun drain(s: CorBInestSink<T>) { s.block() }
}

fun <R> corBInestMakeSrc(x: R, y: R): CorBInestSrc<R> = corBInestMkFlow<R> {
    emit(corBInestProduceOne { x })
    emit(corBInestProduceOne { y })
}

suspend fun corBInestRunSrc(src: CorBInestSrc<Int>): Int {
    val sink = CorBInestListSink<Int>()
    src.drain(sink)
    var sum = 0
    for (x in sink.items) sum += x
    return sum
}

class NestedGenericSuspendLambdaTests {
    @TestAttribute
    fun nestedSuspendLambdaUnderTvRemap() {
        val c = 20; val dd = 22
        val s1 = corBInestMkFlow<Int> { emit(corBInestProduceOne { c }); emit(corBInestProduceOne { dd }) }
        assertEquals(42, blockOn { corBInestRunSrc(s1) })                        // 42
        assertEquals(42, blockOn { corBInestRunSrc(corBInestMakeSrc(30, 12)) })  // 42
    }
}
