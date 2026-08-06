// feature fixture — il-inlsuspendnest: a §4.4ii-materialized SUSPEND carrier whose body NESTS a `newSuspendLambda` under a
// multi-scope enclosing tv remap (the real unsafeFlow/combineTransform flow shape). All decls (TYPES and funs) carry
// the descriptive `nestedGenericSuspend`/`NestedGenericSuspend` stem so their simple names are UNIQUE across this assembly — a shared type name
// (`Sink`/`Source`) or inline-fn name with the sibling flow case (il-inlsuspendflow) collides in bir2cir's suspend
// member cold-entry naming (keyed by owner-type simple name) at runtime (EntryPointNotFound). Driven by the shared
// `dotkt.support.blockOn` harness; the former `main` + golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import dotkt.support.blockOn

interface NestedGenericSuspendSink<T> { suspend fun emit(value: T) }
interface NestedGenericSuspendSource<T> { suspend fun drain(s: NestedGenericSuspendSink<T>) }

class NestedGenericSuspendListSink<T> : NestedGenericSuspendSink<T> {
    val items = ArrayList<T>()
    override suspend fun emit(value: T) { items.add(value) }
}

// a non-inline suspend fn taking a suspend lambda — the lambda literal at the call site becomes a NESTED
// `newSuspendLambda` INSIDE the materialized carrier body (capturing an enclosing local, returning R).
suspend fun <T> nestedGenericSuspendProduceOne(block: suspend () -> T): T = block()

inline fun <T> nestedGenericSuspendMakeFlow(crossinline block: suspend NestedGenericSuspendSink<T>.() -> Unit): NestedGenericSuspendSource<T> = object : NestedGenericSuspendSource<T> {
    override suspend fun drain(s: NestedGenericSuspendSink<T>) { s.block() }
}

fun <R> nestedGenericSuspendMakeSource(x: R, y: R): NestedGenericSuspendSource<R> = nestedGenericSuspendMakeFlow<R> {
    emit(nestedGenericSuspendProduceOne { x })
    emit(nestedGenericSuspendProduceOne { y })
}

suspend fun nestedGenericSuspendRunSource(src: NestedGenericSuspendSource<Int>): Int {
    val sink = NestedGenericSuspendListSink<Int>()
    src.drain(sink)
    var sum = 0
    for (x in sink.items) sum += x
    return sum
}

suspend fun nestedGenericSuspendRunText(src: NestedGenericSuspendSource<String>): String {
    val sink = NestedGenericSuspendListSink<String>()
    src.drain(sink)
    return sink.items[0]
}

fun <A, B, C, D> nestedGenericSuspendSparseMethodSource(x: B, y: D): NestedGenericSuspendSource<String> =
    nestedGenericSuspendMakeFlow<String> { emit(x.toString() + ":" + y.toString()) }

class NestedGenericSuspendOwner<T : Comparable<T>>(private val prefix: T) {
    fun <A, B> make(ignored: A, value: B): NestedGenericSuspendSource<String> =
        nestedGenericSuspendMakeFlow<String> { emit(prefix.toString() + ":" + value.toString()) }
}

class NestedGenericSuspendLambdaTests {
    @TestAttribute
    fun nestedSuspendLambdaUnderTvRemap() {
        val c = 20; val dd = 22
        val s1 = nestedGenericSuspendMakeFlow<Int> { emit(nestedGenericSuspendProduceOne { c }); emit(nestedGenericSuspendProduceOne { dd }) }
        assertEquals(42, blockOn { nestedGenericSuspendRunSource(s1) })                        // 42
        assertEquals(42, blockOn { nestedGenericSuspendRunSource(nestedGenericSuspendMakeSource(30, 12)) })  // 42
        val sparse = nestedGenericSuspendSparseMethodSource<Unit, Int, Unit, String>(42, "ok")
        assertEquals("42:ok", blockOn { nestedGenericSuspendRunText(sparse) })
        val owner = NestedGenericSuspendOwner("owner")
        assertEquals("owner:43", blockOn {
            nestedGenericSuspendRunText(owner.make<Unit, Int>(Unit, 43))
        })
    }
}
