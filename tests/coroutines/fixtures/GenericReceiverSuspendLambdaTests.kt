// feature fixture — il-inlsuspendflow: the GENERIC + RECEIVER + suspend-MEMBER inline-splice path (kotlinx `flow{}`, 2A).
// A generic `inline fun <T>` whose crossinline SUSPEND RECEIVER lambda is captured into an `object : Source<T>` literal
// whose carrier invokes the generic receiver's suspend member `emit` — a MULTI-scope {(method,0),(type,0)} tv key set.
// All declarations (types and functions) use the descriptive `genericReceiverSuspend`/`GenericReceiverSuspend` stem so their simple names are UNIQUE across this
// assembly — bir2cir's cold-core suspend lowering keys top-level suspend funs by simple name AND a suspend member's
// cold entry by its OWNER TYPE simple name, so a shared type name (`Sink`/`Source`) or fn name across two flow-shaped
// cases collides at runtime (EntryPointNotFound). Driven by the shared `dotkt.support.blockOn` harness; the former
// `main` + golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Type
import dotkt.support.blockOn

abstract class GenericReceiverSuspendBaseSink<T> {
    abstract suspend fun emit(value: T)
}

abstract class GenericReceiverSuspendSink<T> : GenericReceiverSuspendBaseSink<T>()

interface GenericReceiverSuspendSource<T> {
    suspend fun drain(s: GenericReceiverSuspendSink<T>)
}

class GenericReceiverSuspendListSink<T> : GenericReceiverSuspendSink<T>() {
    val items = ArrayList<T>()
    override suspend fun emit(value: T) { items.add(value) }
}

inline fun <T> genericReceiverSuspendMakeFlow(crossinline block: suspend GenericReceiverSuspendSink<T>.() -> Unit): GenericReceiverSuspendSource<T> = object : GenericReceiverSuspendSource<T> {
    override suspend fun drain(s: GenericReceiverSuspendSink<T>) { s.block() }
}

fun <R> genericReceiverSuspendMakeSource(x: R, y: R): GenericReceiverSuspendSource<R> = genericReceiverSuspendMakeFlow<R> { emit(x); emit(y) }

class GenericReceiverSuspendBox<E>(val a: E, val b: E) {
    fun make(): GenericReceiverSuspendSource<E> = genericReceiverSuspendMakeFlow<E> { emit(a); emit(b) }
}

// #74: materializing this carrier sees real caller-frame TVs from E/M and foreign declaration-frame TVs from
// genericReceiverSuspendSum's sig and inherited emit's sig. Only E/M belong to the synthesized SM; both sig arrays must stay
// declaration-relative so InheritedMemberOwnerBinding can retarget emit from GenericReceiverSuspendSink to GenericReceiverSuspendBaseSink.
fun <A, B> genericReceiverSuspendSum(a: A, b: B): Int = (a as Int) + (b as Int)
class GenericReceiverSuspendHolder<E>(private val e: E) {
    fun <M> make(m: M): GenericReceiverSuspendSource<Int> = genericReceiverSuspendMakeFlow<Int> { emit(genericReceiverSuspendSum(e, m)) }
}

suspend fun genericReceiverSuspendRunSource(src: GenericReceiverSuspendSource<Int>): Int {
    val sink = GenericReceiverSuspendListSink<Int>()
    src.drain(sink)
    var sum = 0
    for (x in sink.items) sum += x
    return sum
}

class GenericReceiverSuspendLambdaTests {
    @TestAttribute
    fun genericReceiverSuspendMember() {
        val s1 = genericReceiverSuspendMakeFlow<Int> { emit(20); emit(22) }
        assertEquals(42, blockOn { genericReceiverSuspendRunSource(s1) })          // 42
        val s2 = genericReceiverSuspendMakeSource(30, 12)
        assertEquals(42, blockOn { genericReceiverSuspendRunSource(s2) })          // 42
        val s3 = GenericReceiverSuspendBox(40, 2).make()
        assertEquals(42, blockOn { genericReceiverSuspendRunSource(s3) })          // 42
        val s4 = GenericReceiverSuspendHolder(41).make(1)
        assertEquals(42, blockOn { genericReceiverSuspendRunSource(s4) })          // 42
        // The SM closes only over M and E. Before #74 the two foreign sig frames added a spurious third parameter.
        assertEquals(2, Type.GetType("GenericReceiverSuspendHolder_make_lambda3\$sm`2")!!.GetGenericArguments().size)
    }
}
