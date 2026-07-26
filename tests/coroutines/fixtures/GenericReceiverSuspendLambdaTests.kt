// CorB batch — il-inlsuspendflow: the GENERIC + RECEIVER + suspend-MEMBER inline-splice path (kotlinx `flow{}`, 2A).
// A generic `inline fun <T>` whose crossinline SUSPEND RECEIVER lambda is captured into an `object : Src<T>` literal
// whose carrier invokes the generic receiver's suspend member `emit` — a MULTI-scope {(method,0),(type,0)} tv key set.
// All decls (TYPES and funs) carry the `iflow`/`CorBIflow` case token so their simple names are UNIQUE across this
// assembly — bir2cir's cold-core suspend lowering keys top-level suspend funs by simple name AND a suspend member's
// cold entry by its OWNER TYPE simple name, so a shared type name (`Sink`/`Src`) or fn name across two flow-shaped
// cases collides at runtime (EntryPointNotFound). Driven by the shared `dotkt.support.blockOn` harness; the former
// `main` + golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Type
import dotkt.support.blockOn

abstract class CorBIflowBaseSink<T> {
    abstract suspend fun emit(value: T)
}

abstract class CorBIflowSink<T> : CorBIflowBaseSink<T>()

interface CorBIflowSrc<T> {
    suspend fun drain(s: CorBIflowSink<T>)
}

class CorBIflowListSink<T> : CorBIflowSink<T>() {
    val items = ArrayList<T>()
    override suspend fun emit(value: T) { items.add(value) }
}

inline fun <T> corBIflowMkFlow(crossinline block: suspend CorBIflowSink<T>.() -> Unit): CorBIflowSrc<T> = object : CorBIflowSrc<T> {
    override suspend fun drain(s: CorBIflowSink<T>) { s.block() }
}

fun <R> corBIflowMakeSrc(x: R, y: R): CorBIflowSrc<R> = corBIflowMkFlow<R> { emit(x); emit(y) }

class CorBIflowBox<E>(val a: E, val b: E) {
    fun make(): CorBIflowSrc<E> = corBIflowMkFlow<E> { emit(a); emit(b) }
}

// #74: materializing this carrier sees real caller-frame TVs from E/M and foreign declaration-frame TVs from
// corBIflowSum's sig and inherited emit's sig. Only E/M belong to the synthesized SM; both sig arrays must stay
// declaration-relative so InheritedMemberOwnerBinding can retarget emit from CorBIflowSink to CorBIflowBaseSink.
fun <A, B> corBIflowSum(a: A, b: B): Int = (a as Int) + (b as Int)
class CorBIflowHolder<E>(private val e: E) {
    fun <M> make(m: M): CorBIflowSrc<Int> = corBIflowMkFlow<Int> { emit(corBIflowSum(e, m)) }
}

suspend fun corBIflowRunSrc(src: CorBIflowSrc<Int>): Int {
    val sink = CorBIflowListSink<Int>()
    src.drain(sink)
    var sum = 0
    for (x in sink.items) sum += x
    return sum
}

class GenericReceiverSuspendLambdaTests {
    @TestAttribute
    fun genericReceiverSuspendMember() {
        val s1 = corBIflowMkFlow<Int> { emit(20); emit(22) }
        assertEquals(42, blockOn { corBIflowRunSrc(s1) })          // 42
        val s2 = corBIflowMakeSrc(30, 12)
        assertEquals(42, blockOn { corBIflowRunSrc(s2) })          // 42
        val s3 = CorBIflowBox(40, 2).make()
        assertEquals(42, blockOn { corBIflowRunSrc(s3) })          // 42
        val s4 = CorBIflowHolder(41).make(1)
        assertEquals(42, blockOn { corBIflowRunSrc(s4) })          // 42
        // The SM closes only over M and E. Before #74 the two foreign sig frames added a spurious third parameter.
        assertEquals(2, Type.GetType("CorBIflowHolder_make_lambda3\$sm`2")!!.GetGenericArguments().size)
    }
}
