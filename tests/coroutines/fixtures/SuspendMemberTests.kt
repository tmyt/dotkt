// Cold-core suspend MEMBER battery (CorA batch): instance members, super/interface-inherited members, generic-class
// instance members, a @RestrictsSuspension user scope, and a genuinely-suspending `suspend fun main` drain. Each old
// `main` + stdout golden becomes one @TestAttribute method; the cold cases are driven by the shared
// `dotkt.support.blockOn` harness (corestrict drives its own receiver-form startCoroutine, no blockOn needed).
//
// Coverage preserved (old case -> method):
//   il-coldinst    -> coldInst_instanceAndCrossFileSuspendCalls  (P3 wave-2a: $this field, member/cross-file calls)
//   il-coldsuper   -> coldSuper_inheritedSuspendMemberDispatch    (#78/#90: base/super-iface decl, mutual recursion)
//   il-coldvirt    -> coldVirt_genericClassInstanceMember         (P5 A1b: generic-class instance-member SM)
//   il-corestrict  -> coRestrict_userRestrictsSuspensionScope     (a hand-authored @RestrictsSuspension receiver)
//   il-comaindrain -> coMainDrain_genuinelySuspendingMainBlocks   (BUG 4: real Task.Delay await, threadpool resume)
//
// Top-level names are family-prefixed (`corAInst`/`corASup`/`corAVirt`/`corARes`/`corADrain`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Tasks.Task
import kotlin.clr.await
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.RestrictsSuspension
import kotlin.coroutines.startCoroutine
import dotkt.support.blockOn

// ---- il-coldinst: instance suspend members + member/cross-file suspend calls ---------------------------------
class CorAInstCounter(var n: Int) {
    suspend fun bump(): Int { n += 1; return n }           // instance member, no suspension -> direct instance cold entry
}
class CorAInstSvc(val base: Int) {
    suspend fun helper(): Int { return base }
    suspend fun chain(): Int { val h = helper(); return h + this.helper() }   // $this field + spilled local
}
class CorAInstBox<T>(val v: T) {
    suspend fun get(): T = v                                // instance + generic class (member cold entry over T)
}
suspend fun corAInstTopUse(): Int { val c = CorAInstCounter(100); return c.bump() }  // top-level -> member call
suspend fun corAInstCrossFileVal(): Int = 7               // was a SECOND source file (same-assembly cross-file rewrite)

// ---- il-coldsuper: a suspend member declared on a SUPERtype, called via a subclass receiver ------------------
suspend fun corASupEcho(x: Int): Int = x
abstract class CorASupBase {
    suspend fun awaitInternal(): Int { return corASupEcho(10) }        // declared on the BASE
}
class CorASupDerived : CorASupBase() {
    suspend fun await(): Int { return awaitInternal() + 1 }            // member declared on Base
}
interface CorASupSource {
    suspend fun receiveOrNull(): Int                                   // declared on the super-INTERFACE
}
open class CorASupChannelBase : CorASupSource {
    override suspend fun receiveOrNull(): Int = corASupEcho(41)
}
class CorASupChannelImpl : CorASupChannelBase() {
    suspend fun consume(): Int = receiveOrNull() + 1                   // decl on ChannelBase/Source
}
suspend fun corASupPing(n: Int): Int { if (n <= 0) return 0; return 1 + corASupPong(n - 1) }  // mutual recursion
suspend fun corASupPong(n: Int): Int { if (n <= 0) return 0; return 1 + corASupPing(n - 1) }

// ---- il-coldvirt: a suspending instance member of a GENERIC class (P5 A1b) -----------------------------------
suspend fun <T> corAVirtEcho(x: T): T = x
class CorAVirtBox<T>(val v: T) {
    suspend fun getTwice(): T { val a = corAVirtEcho(v); return a }    // member SM generic over the class's T
}

// ---- il-corestrict: a USER-DEFINED @RestrictsSuspension receiver, driven by receiver-form startCoroutine ------
@RestrictsSuspension
class CorAResCollector<T> {
    val items = ArrayList<T>()
    suspend fun add(value: T) { items.add(value) }                     // sync-completion direct cold-entry path
    suspend fun addAll(values: List<T>) { for (v in values) add(v) }   // control-flow across a suspend call
}
private class CorAResDone : Continuation<Unit> {
    var err: Throwable? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<Unit>) { err = result.exceptionOrNull() }
}
fun <T> corAResCollect(block: suspend CorAResCollector<T>.() -> Unit): List<T> {
    val c = CorAResCollector<T>()
    val d = CorAResDone()
    block.startCoroutine(c, d)   // receiver-form startCoroutine drives the restricted scope
    d.err?.let { throw it }
    return c.items
}

// ---- il-comaindrain: a genuinely-suspending main that awaits a real INCOMPLETE .NET Task -------------------
suspend fun corADrainCompute(): Int {
    Task.Delay(1).await()   // genuine suspension: resumes on a threadpool thread
    return 42
}

class SuspendMemberTests {
    @TestAttribute
    fun instanceAndCrossFileSuspendCalls() {
        val c = CorAInstCounter(10)
        assertEquals(11, blockOn { c.bump() })            // 11
        assertEquals(12, blockOn { c.bump() })            // 12
        val s = CorAInstSvc(5)
        assertEquals(10, blockOn { s.chain() })           // 5 + 5 = 10
        val b = CorAInstBox(42)
        assertEquals(42, blockOn { b.get() })             // 42
        val bs = CorAInstBox("hi")
        assertEquals("hi", blockOn { bs.get() })          // hi
        assertEquals(101, blockOn { corAInstTopUse() })   // 101
        assertEquals(7, blockOn { corAInstCrossFileVal() })// 7
    }

    @TestAttribute
    fun inheritedSuspendMemberDispatch() {
        assertEquals(11, blockOn { CorASupDerived().await() })         // 11
        assertEquals(42, blockOn { CorASupChannelImpl().consume() })   // 42
        assertEquals(5, blockOn { corASupPing(5) })                    // 5
    }

    @TestAttribute
    fun genericClassInstanceMember() {
        assertEquals(42, blockOn { CorAVirtBox(42).getTwice() })       // T = Int (value type)
        assertEquals("hi", blockOn { CorAVirtBox("hi").getTwice() })   // T = String (reference type)
    }

    @TestAttribute
    fun userRestrictsSuspensionScope() {
        val xs = corAResCollect<Int> {
            add(1)
            add(2)
            addAll(listOf(3, 4, 5))
        }
        assertEquals("1,2,3,4,5", xs.joinToString(","))   // 1,2,3,4,5
        assertEquals(5, xs.size)                           // 5
        val ss = corAResCollect<String> {
            add("a")
            add("b")
        }
        assertEquals("a-b", ss.joinToString("-"))          // a-b
    }

    @TestAttribute
    fun genuinelySuspendingMainBlocks() {
        assertEquals(42, blockOn { corADrainCompute() })   // 42 — printed only if the drain blocks until the async resume
    }
}
