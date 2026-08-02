// feature fixture — il-inlsuspendflow: the GENERIC + RECEIVER + suspend-MEMBER inline-splice path (kotlinx `flow{}`, 2A).
// A generic `inline fun <T>` whose crossinline SUSPEND RECEIVER lambda is captured into an `object : Src<T>` literal
// whose carrier invokes the generic receiver's suspend member `emit` — a MULTI-scope {(method,0),(type,0)} tv key set.
// All decls (TYPES and funs) carry the `iflow`/`GenericReceiverSuspendIflow` case token so their simple names are UNIQUE across this
// assembly — bir2cir's cold-core suspend lowering keys top-level suspend funs by simple name AND a suspend member's
// cold entry by its OWNER TYPE simple name, so a shared type name (`Sink`/`Src`) or fn name across two flow-shaped
// cases collides at runtime (EntryPointNotFound). Driven by the shared `dotkt.support.blockOn` harness; the former
// `main` + golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Type
import dotkt.support.blockOn

abstract class GenericReceiverSuspendIflowBaseSink<T> {
    abstract suspend fun emit(value: T)
}

abstract class GenericReceiverSuspendIflowSink<T> : GenericReceiverSuspendIflowBaseSink<T>()

interface GenericReceiverSuspendIflowSrc<T> {
    suspend fun drain(s: GenericReceiverSuspendIflowSink<T>)
}

class GenericReceiverSuspendIflowListSink<T> : GenericReceiverSuspendIflowSink<T>() {
    val items = ArrayList<T>()
    override suspend fun emit(value: T) { items.add(value) }
}

inline fun <T> genericReceiverSuspendIflowMkFlow(crossinline block: suspend GenericReceiverSuspendIflowSink<T>.() -> Unit): GenericReceiverSuspendIflowSrc<T> = object : GenericReceiverSuspendIflowSrc<T> {
    override suspend fun drain(s: GenericReceiverSuspendIflowSink<T>) { s.block() }
}

fun <R> genericReceiverSuspendIflowMakeSrc(x: R, y: R): GenericReceiverSuspendIflowSrc<R> = genericReceiverSuspendIflowMkFlow<R> { emit(x); emit(y) }

class GenericReceiverSuspendIflowBox<E>(val a: E, val b: E) {
    fun make(): GenericReceiverSuspendIflowSrc<E> = genericReceiverSuspendIflowMkFlow<E> { emit(a); emit(b) }
}

// #74: materializing this carrier sees real caller-frame TVs from E/M and foreign declaration-frame TVs from
// genericReceiverSuspendIflowSum's sig and inherited emit's sig. Only E/M belong to the synthesized SM; both sig arrays must stay
// declaration-relative so InheritedMemberOwnerBinding can retarget emit from GenericReceiverSuspendIflowSink to GenericReceiverSuspendIflowBaseSink.
fun <A, B> genericReceiverSuspendIflowSum(a: A, b: B): Int = (a as Int) + (b as Int)
class GenericReceiverSuspendIflowHolder<E>(private val e: E) {
    fun <M> make(m: M): GenericReceiverSuspendIflowSrc<Int> = genericReceiverSuspendIflowMkFlow<Int> { emit(genericReceiverSuspendIflowSum(e, m)) }
}

suspend fun genericReceiverSuspendIflowRunSrc(src: GenericReceiverSuspendIflowSrc<Int>): Int {
    val sink = GenericReceiverSuspendIflowListSink<Int>()
    src.drain(sink)
    var sum = 0
    for (x in sink.items) sum += x
    return sum
}

class GenericReceiverSuspendLambdaTests {
    @TestAttribute
    fun genericReceiverSuspendMember() {
        val s1 = genericReceiverSuspendIflowMkFlow<Int> { emit(20); emit(22) }
        assertEquals(42, blockOn { genericReceiverSuspendIflowRunSrc(s1) })          // 42
        val s2 = genericReceiverSuspendIflowMakeSrc(30, 12)
        assertEquals(42, blockOn { genericReceiverSuspendIflowRunSrc(s2) })          // 42
        val s3 = GenericReceiverSuspendIflowBox(40, 2).make()
        assertEquals(42, blockOn { genericReceiverSuspendIflowRunSrc(s3) })          // 42
        val s4 = GenericReceiverSuspendIflowHolder(41).make(1)
        assertEquals(42, blockOn { genericReceiverSuspendIflowRunSrc(s4) })          // 42
        // The SM closes only over M and E. Before #74 the two foreign sig frames added a spurious third parameter.
        assertEquals(2, Type.GetType("GenericReceiverSuspendIflowHolder_make_lambda3\$sm`2")!!.GetGenericArguments().size)
    }
}
