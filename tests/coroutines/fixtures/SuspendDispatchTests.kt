// R1 (#90) cold-entry DISPATCH battery (feature fixture): abstract-class, base-inherited, subtype-interface, generic-
// interface-DIM, and static/companion suspend members — the "every super-declared suspend member has a virtual cold
// entry by unconditional declaration" family. Each old `main` + stdout golden becomes one @TestAttribute method
// driven by the shared `dotkt.support.blockOn` cold-core harness (every suspension completes synchronously).
//
// Coverage preserved (old case -> method):
//   il-coldabstract      -> coldAbstract_abstractClassSuspendVtable  (BUG 3: abstract cold entry + Task bridge, virtual dispatch)
//   il-coldbaseinherit   -> coldBaseInherit_baseDeclaredNoOverride   (R1: base-declared suspend fun via subclass receiver)
//   il-coldsubiface      -> coldSubIface_interfaceMemberViaSubtype   (R1: interface suspend member via subtype receiver)
//   il-colddimgen        -> coldDimGen_genericInterfaceDefaultMethod (R1: a defaulted generic-interface suspend DIM)
//   il-coldstaticmember  -> coldStaticMember_companionAndObjectMember(R1 M3: static cold-entry decl + object-member drive)
//
// Top-level names are family-prefixed (`suspendDispatchAbstract`/`suspendDispatchBase`/`suspendDispatchSubtype`/`suspendDispatchDefaultInterface`/`suspendDispatchStaticMember`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import dotkt.support.blockOn

// ---- il-coldabstract: an abstract-class suspend member round-trips its full vtable shape ---------------------
abstract class SuspendDispatchAbstractBase { abstract suspend fun poll(): Int }
class SuspendDispatchAbstractImpl(val n: Int) : SuspendDispatchAbstractBase() { override suspend fun poll(): Int = n + 1 }

// ---- il-coldbaseinherit: a base-declared suspend fun called via a subclass receiver, no override -------------
open class SuspendDispatchBaseReader(val seed: Int) {
    open suspend fun read(): Int = seed + 1
}
class SuspendDispatchBaseFastReader(seed: Int) : SuspendDispatchBaseReader(seed)
suspend fun suspendDispatchBaseDrive(r: SuspendDispatchBaseFastReader): Int = r.read()

// ---- il-coldsubiface: an interface suspend member called through a SUBTYPE static receiver -------------------
interface SuspendDispatchSubtypeProducer { suspend fun produce(): Int }
class SuspendDispatchSubtypeNumberProducer(val base: Int) : SuspendDispatchSubtypeProducer {
    override suspend fun produce(): Int = base + 1
}
suspend fun suspendDispatchSubtypeDrive(p: SuspendDispatchSubtypeNumberProducer): Int = p.produce()

// ---- il-colddimgen: a defaulted generic-interface suspend method (a DIM), the Channel<E>.receiveOrNull shape --
interface SuspendDispatchDefaultInterfaceSource<E> {
    suspend fun fetch(): E
    suspend fun fetchOrDefault(fallback: E): E {
        val v = fetch()
        return v
    }
}
class SuspendDispatchDefaultInterfaceIntSource(val v: Int) : SuspendDispatchDefaultInterfaceSource<Int> {
    override suspend fun fetch(): Int = v
}
suspend fun suspendDispatchDefaultInterfaceDrive(s: SuspendDispatchDefaultInterfaceSource<Int>): Int = s.fetchOrDefault(0)

// A constructed generic receiver's suspend result must be substituted before the caller SM copies it into an
// await field. Keeping the callee-relative `type TV0` until after SM synthesis makes this field `object`, followed by
// invalid `object + int` IL in the resumed branch.
interface SuspendDispatchConstructedSource<E> { suspend fun read(): E }
class SuspendDispatchConstructedIntSource(private val value: Int) : SuspendDispatchConstructedSource<Int> {
    override suspend fun read(): Int = value
}
suspend fun suspendDispatchConstructedUse(source: SuspendDispatchConstructedSource<Int>): Int {
    val value = source.read()
    return value + 2
}

// ---- il-coldstaticmember: a static/companion suspend member's cold-entry declaration (M3) --------------------
suspend fun suspendDispatchStaticMemberBump(x: Int): Int = x + 1
class SuspendDispatchStaticMemberCalc {
    companion object {
        // A companion suspend member -> a STATIC cold entry on SuspendDispatchStaticMemberCalc (M3): emitted + ilverify-verified as a
        // DECLARATION (the same-assembly companion-call fact is a kotc gap, so it is not driven at runtime here).
        suspend fun compute(): Int = suspendDispatchStaticMemberBump(41)
    }
}
object SuspendDispatchStaticMemberTicker {
    suspend fun tick(): Int = suspendDispatchStaticMemberBump(41)   // an object-instance suspend member drives the runtime assertion
}

class SuspendDispatchTests {
    @TestAttribute
    fun abstractClassSuspendVtable() {
        val b: SuspendDispatchAbstractBase = SuspendDispatchAbstractImpl(41)
        assertEquals(42, blockOn { b.poll() })   // 42 — virtual dispatch through the abstract cold entry
    }

    @TestAttribute
    fun baseDeclaredNoOverride() {
        assertEquals(42, blockOn { suspendDispatchBaseDrive(SuspendDispatchBaseFastReader(41)) })   // 42
    }

    @TestAttribute
    fun interfaceMemberViaSubtype() {
        assertEquals(42, blockOn { suspendDispatchSubtypeDrive(SuspendDispatchSubtypeNumberProducer(41)) })   // 42
    }

    @TestAttribute
    fun genericInterfaceDefaultMethod() {
        assertEquals(42, blockOn { suspendDispatchDefaultInterfaceDrive(SuspendDispatchDefaultInterfaceIntSource(42)) })   // 42
    }

    @TestAttribute
    fun constructedGenericSuspendResult() {
        assertEquals(42, blockOn { suspendDispatchConstructedUse(SuspendDispatchConstructedIntSource(40)) })
    }

    @TestAttribute
    fun companionAndObjectMember() {
        assertEquals(42, blockOn { SuspendDispatchStaticMemberTicker.tick() })   // 42
    }
}
