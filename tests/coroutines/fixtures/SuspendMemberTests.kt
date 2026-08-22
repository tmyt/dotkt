// Cold-core suspend MEMBER battery (feature fixture): instance members, super/interface-inherited members, generic-class
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
// Top-level names are family-prefixed (`suspendMemberInstance`/`suspendMemberInherited`/`suspendMemberGeneric`/`suspendMemberRestricted`/`suspendMemberDrain`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Threading.Tasks.Task
import System.Threading.Tasks.Task1
import System.Type
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.RestrictsSuspension
import kotlin.coroutines.startCoroutine
import dotkt.support.blockOn

// ---- il-coldinst: instance suspend members + member/cross-file suspend calls ---------------------------------
class SuspendMemberInstanceCounter(var n: Int) {
    suspend fun bump(): Int { n += 1; return n }           // instance member, no suspension -> direct instance cold entry
}
class SuspendMemberInstanceService(val base: Int) {
    suspend fun helper(): Int { return base }
    suspend fun chain(): Int { val h = helper(); return h + this.helper() }   // $this field + spilled local
}
class SuspendMemberInstanceBox<T>(val v: T) {
    suspend fun get(): T = v                                // instance + generic class (member cold entry over T)
}
suspend fun suspendMemberInstanceTopUse(): Int { val c = SuspendMemberInstanceCounter(100); return c.bump() }  // top-level -> member call
suspend fun suspendMemberInstanceCrossFileValue(): Int = 7               // was a SECOND source file (same-assembly cross-file rewrite)

// ---- il-coldsuper: a suspend member declared on a SUPERtype, called via a subclass receiver ------------------
suspend fun suspendMemberInheritedEcho(x: Int): Int = x
abstract class SuspendMemberInheritedBase {
    suspend fun awaitInternal(): Int { return suspendMemberInheritedEcho(10) }        // declared on the BASE
}
class SuspendMemberInheritedDerived : SuspendMemberInheritedBase() {
    suspend fun await(): Int { return awaitInternal() + 1 }            // member declared on Base
}
interface SuspendMemberInheritedSource {
    suspend fun receiveOrNull(): Int                                   // declared on the super-INTERFACE
}
open class SuspendMemberInheritedChannelBase : SuspendMemberInheritedSource {
    override suspend fun receiveOrNull(): Int = suspendMemberInheritedEcho(41)
}
class SuspendMemberInheritedChannelImpl : SuspendMemberInheritedChannelBase() {
    suspend fun consume(): Int = receiveOrNull() + 1                   // decl on ChannelBase/Source
}
suspend fun suspendMemberInheritedPing(n: Int): Int { if (n <= 0) return 0; return 1 + suspendMemberInheritedPong(n - 1) }  // mutual recursion
suspend fun suspendMemberInheritedPong(n: Int): Int { if (n <= 0) return 0; return 1 + suspendMemberInheritedPing(n - 1) }

open class SuspendMemberCovariantValue(val value: Int)
class SuspendMemberNarrowCovariantValue(value: Int) : SuspendMemberCovariantValue(value)
interface SuspendMemberCovariantSlot {
    suspend fun loadCovariant(): SuspendMemberCovariantValue
}
class SuspendMemberCovariantImplementation : SuspendMemberCovariantSlot {
    override suspend fun loadCovariant(): SuspendMemberNarrowCovariantValue =
        SuspendMemberNarrowCovariantValue(46)
}

private fun invokeSuspendMemberCovariantTask(
    implementation: SuspendMemberCovariantImplementation
): Task1<SuspendMemberCovariantValue> =
    Type.GetType("SuspendMemberCovariantSlot")!!
        .GetMethod("loadCovariant")!!
        .Invoke(implementation, null) as Task1<SuspendMemberCovariantValue>

// ---- il-coldvirt: a suspending instance member of a GENERIC class (P5 A1b) -----------------------------------
suspend fun <T> suspendMemberGenericEcho(x: T): T = x
class SuspendMemberGenericBox<T>(val v: T) {
    suspend fun getTwice(): T { val a = suspendMemberGenericEcho(v); return a }    // member SM generic over the class's T
}

// ---- il-corestrict: a USER-DEFINED @RestrictsSuspension receiver, driven by receiver-form startCoroutine ------
@RestrictsSuspension
class SuspendMemberRestrictedCollector<T> {
    val items = ArrayList<T>()
    suspend fun add(value: T) { items.add(value) }                     // sync-completion direct cold-entry path
    suspend fun addAll(values: List<T>) { for (v in values) add(v) }   // control-flow across a suspend call
}
private class SuspendMemberRestrictedDone : Continuation<Unit> {
    var err: Throwable? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<Unit>) { err = result.exceptionOrNull() }
}
fun <T> suspendMemberRestrictedCollect(block: suspend SuspendMemberRestrictedCollector<T>.() -> Unit): List<T> {
    val c = SuspendMemberRestrictedCollector<T>()
    val d = SuspendMemberRestrictedDone()
    block.startCoroutine(c, d)   // receiver-form startCoroutine drives the restricted scope
    d.err?.let { throw it }
    return c.items
}

// ---- il-comaindrain: a genuinely-suspending main that awaits a real INCOMPLETE .NET Task -------------------
suspend fun suspendMemberDrainCompute(): Int {
    Task.Delay(1).await()   // genuine suspension: resumes on a threadpool thread
    return 42
}

class SuspendMemberTests {
    @TestAttribute
    fun instanceAndCrossFileSuspendCalls() {
        val c = SuspendMemberInstanceCounter(10)
        assertEquals(11, blockOn { c.bump() })            // 11
        assertEquals(12, blockOn { c.bump() })            // 12
        val s = SuspendMemberInstanceService(5)
        assertEquals(10, blockOn { s.chain() })           // 5 + 5 = 10
        val b = SuspendMemberInstanceBox(42)
        assertEquals(42, blockOn { b.get() })             // 42
        val bs = SuspendMemberInstanceBox("hi")
        assertEquals("hi", blockOn { bs.get() })          // hi
        assertEquals(101, blockOn { suspendMemberInstanceTopUse() })   // 101
        assertEquals(7, blockOn { suspendMemberInstanceCrossFileValue() })// 7
    }

    @TestAttribute
    fun inheritedSuspendMemberDispatch() {
        assertEquals(11, blockOn { SuspendMemberInheritedDerived().await() })         // 11
        assertEquals(42, blockOn { SuspendMemberInheritedChannelImpl().consume() })   // 42
        assertEquals(5, blockOn { suspendMemberInheritedPing(5) })                    // 5
        val implementation = SuspendMemberCovariantImplementation()
        assertEquals(46, blockOn {
            val slot: SuspendMemberCovariantSlot = implementation
            slot.loadCovariant().value
        })
        assertEquals(46, blockOn { implementation.loadCovariant().value })
        assertEquals(46, invokeSuspendMemberCovariantTask(implementation).Result.value)
    }

    @TestAttribute
    fun genericClassInstanceMember() {
        assertEquals(42, blockOn { SuspendMemberGenericBox(42).getTwice() })       // T = Int (value type)
        assertEquals("hi", blockOn { SuspendMemberGenericBox("hi").getTwice() })   // T = String (reference type)
    }

    @TestAttribute
    fun userRestrictsSuspensionScope() {
        val xs = suspendMemberRestrictedCollect<Int> {
            add(1)
            add(2)
            addAll(listOf(3, 4, 5))
        }
        assertEquals("1,2,3,4,5", xs.joinToString(","))   // 1,2,3,4,5
        assertEquals(5, xs.size)                           // 5
        val ss = suspendMemberRestrictedCollect<String> {
            add("a")
            add("b")
        }
        assertEquals("a-b", ss.joinToString("-"))          // a-b
    }

    @TestAttribute
    fun genuinelySuspendingMainBlocks() {
        assertEquals(42, blockOn { suspendMemberDrainCompute() })   // 42 — printed only if the drain blocks until the async resume
    }
}
