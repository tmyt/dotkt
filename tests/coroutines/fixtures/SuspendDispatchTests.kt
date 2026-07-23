// R1 (#90) cold-entry DISPATCH battery (CorA batch): abstract-class, base-inherited, subtype-interface, generic-
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
// Top-level names are family-prefixed (`corAAbs`/`corABase`/`corASub`/`corADim`/`corAStat`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

// ---- il-coldabstract: an abstract-class suspend member round-trips its full vtable shape ---------------------
abstract class CorAAbsBase { abstract suspend fun poll(): Int }
class CorAAbsImpl(val n: Int) : CorAAbsBase() { override suspend fun poll(): Int = n + 1 }

// ---- il-coldbaseinherit: a base-declared suspend fun called via a subclass receiver, no override -------------
open class CorABaseReader(val seed: Int) {
    open suspend fun read(): Int = seed + 1
}
class CorABaseFastReader(seed: Int) : CorABaseReader(seed)
suspend fun corABaseDrive(r: CorABaseFastReader): Int = r.read()

// ---- il-coldsubiface: an interface suspend member called through a SUBTYPE static receiver -------------------
interface CorASubProducer { suspend fun produce(): Int }
class CorASubNumberProducer(val base: Int) : CorASubProducer {
    override suspend fun produce(): Int = base + 1
}
suspend fun corASubDrive(p: CorASubNumberProducer): Int = p.produce()

// ---- il-colddimgen: a defaulted generic-interface suspend method (a DIM), the Channel<E>.receiveOrNull shape --
interface CorADimSource<E> {
    suspend fun fetch(): E
    suspend fun fetchOrDefault(fallback: E): E {
        val v = fetch()
        return v
    }
}
class CorADimIntSource(val v: Int) : CorADimSource<Int> {
    override suspend fun fetch(): Int = v
}
suspend fun corADimDrive(s: CorADimSource<Int>): Int = s.fetchOrDefault(0)

// A constructed generic receiver's suspend result must be substituted before the caller SM copies it into an
// await field. Keeping the callee-relative `type TV0` until after SM synthesis makes this field `object`, followed by
// invalid `object + int` IL in the resumed branch.
interface CorAConstructedSource<E> { suspend fun read(): E }
class CorAConstructedIntSource(private val value: Int) : CorAConstructedSource<Int> {
    override suspend fun read(): Int = value
}
suspend fun corAConstructedUse(source: CorAConstructedSource<Int>): Int {
    val value = source.read()
    return value + 2
}

// ---- il-coldstaticmember: a static/companion suspend member's cold-entry declaration (M3) --------------------
suspend fun corAStatBump(x: Int): Int = x + 1
class CorAStatCalc {
    companion object {
        // A companion suspend member -> a STATIC cold entry on CorAStatCalc (M3): emitted + ilverify-verified as a
        // DECLARATION (the same-assembly companion-call fact is a kotc gap, so it is not driven at runtime here).
        suspend fun compute(): Int = corAStatBump(41)
    }
}
object CorAStatTicker {
    suspend fun tick(): Int = corAStatBump(41)   // an object-instance suspend member drives the runtime assertion
}

class SuspendDispatchTests {
    @TestAttribute
    fun abstractClassSuspendVtable() {
        val b: CorAAbsBase = CorAAbsImpl(41)
        assertEquals(42, blockOn { b.poll() })   // 42 — virtual dispatch through the abstract cold entry
    }

    @TestAttribute
    fun baseDeclaredNoOverride() {
        assertEquals(42, blockOn { corABaseDrive(CorABaseFastReader(41)) })   // 42
    }

    @TestAttribute
    fun interfaceMemberViaSubtype() {
        assertEquals(42, blockOn { corASubDrive(CorASubNumberProducer(41)) })   // 42
    }

    @TestAttribute
    fun genericInterfaceDefaultMethod() {
        assertEquals(42, blockOn { corADimDrive(CorADimIntSource(42)) })   // 42
    }

    @TestAttribute
    fun constructedGenericSuspendResult() {
        assertEquals(42, blockOn { corAConstructedUse(CorAConstructedIntSource(40)) })
    }

    @TestAttribute
    fun companionAndObjectMember() {
        assertEquals(42, blockOn { CorAStatTicker.tick() })   // 42
    }
}
