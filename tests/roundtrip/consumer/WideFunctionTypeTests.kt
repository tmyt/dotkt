// #220 — the CANONICAL wide function-type ABI, consumer side. Arities 17..22 have ONE delegate definition, in the
// stdlib, so a wide function type is legal in a public signature. These tests consume ../producer (and ../producer-mpp)
// through their BUILT dlls, which is the only way the property under test can be observed: within one assembly a
// per-assembly definition is indistinguishable from a shared one.
//
// What each test pins, and what it caught before the fix:
//   * parameter position   — worked already (a call-site rewrap hid the split identity); kept as the control.
//   * return position      — the consumer used to define its OWN KFunc`18 and callvirt through it: ILVerify
//                            StackUnexpected (this suite ilverifies, so that finding is a hard failure here).
//   * nested in a generic  — `List<(...)->R>` used to abort ilemit with "no referenced method matches the resolved
//                            descriptor"; the call did not compile at all.
//   * two arities, one producer — 17 and 22 side by side used to break KLIB re-import (the arity-clash rename), so
//                            the consumer failed in the FRONTEND.
//   * two producers, one arity  — a single function value must flow into both; and the two declared parameter types
//                            must be the same Reflection type, owned by the stdlib assembly.
//   * no local definition  — the consumer assembly must declare no KFunc/KAction TypeDef of its own.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsNotNull as assertNotNull
import System.Type
import roundtrip.wide.action17
import roundtrip.wide.action22
import roundtrip.wide.applyNested17
import roundtrip.wide.applyNested22
import roundtrip.wide.nested17
import roundtrip.wide.nested22
import roundtrip.wide.param17
import roundtrip.wide.param22
import roundtrip.wide.paramExt17
import roundtrip.wide.ret17
import roundtrip.wide.ret22
import roundtrip.wide.mpp.mppParam17
import roundtrip.wide.mpp.mppRet17

// A class of this consumer assembly, used only as the reflection entry point to the assembly itself.
class WideFunctionTypeAnchor

class WideFunctionTypeTests {
    // The control position. It already worked: a literal lambda passed to a wide parameter was rewrapped into the
    // callee's own delegate type at the call site, so the split identity never surfaced.
    @TestAttribute
    fun aWideFunctionTypeInParameterPosition() {
        assertEquals(18, param17({ p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17 -> p1 + p17 }))
        assertEquals(23, param22({ p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17, p18, p19, p20, p21, p22 -> p1 + p22 }))
        assertEquals(18, paramExt17({ a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16 -> this + a16 }))
    }

    // Return position: the consumer must invoke the delegate the producer declared, not a same-shaped local copy.
    // The wrong copy still RAN (the JIT does not verify), so the real assertion here is the suite's ILVerify pass.
    @TestAttribute
    fun aWideFunctionTypeInReturnPosition() {
        assertEquals(18, ret17()(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17))
        assertEquals(23, ret22()(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22))
        val stored: (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int = ret17()
        assertEquals(18, stored(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17))
    }

    // Nested in a generic. `List<(...)->R>` is a fully CONCRETE type node, which used to take the exact-equality
    // branch of ilemit's signature match against a nominally different local delegate and abort the emit.
    @TestAttribute
    fun aWideFunctionTypeNestedInAGeneric() {
        assertEquals(17, nested17()[0](1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17))
        assertEquals(17, applyNested17(nested17()))
        assertEquals(22, nested22()[0](1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22))
        assertEquals(22, applyNested22(nested22()))
    }

    // The Unit-returning half of the family (KAction`N). A capturing lambda also exercises the closure build.
    @TestAttribute
    fun aWideUnitReturningFunctionType() {
        var seen17 = 0
        action17({ p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17 -> seen17 = p1 + p17 })
        assertEquals(18, seen17)
        var seen22 = 0
        action22({ p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17, p18, p19, p20, p21, p22 -> seen22 = p1 + p22 })
        assertEquals(23, seen22)
    }

    // One function VALUE flowing into two independently-compiled producers. If each producer named its own
    // delegate type, no single Kotlin value could satisfy both parameters.
    @TestAttribute
    fun twoProducersShareOneDelegateIdentity() {
        val shared: (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int = { p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17 -> p1 - p17 }
        assertEquals(-16, param17(shared))
        assertEquals(-16, mppParam17(shared))
        assertEquals(117, param17(mppRet17()))
        assertEquals(18, mppParam17(ret17()))
    }

    // The identity claim, stated directly: the two producers' declared parameter types are the SAME Reflection
    // type, and it is owned by the stdlib assembly rather than by either producer.
    @TestAttribute
    fun theCanonicalDelegateIsOneTypeOwnedByTheStdlib() {
        val a = wideParameterType("roundtrip.wide.WideFunctionTypesKt, RoundtripProducer", "param17")
        val b = wideParameterType("roundtrip.wide.mpp.MppWideKt, RoundtripProducerMpp", "mppParam17")
        assertTrue(a == b)
        assertEquals("KFunc`18", a.Name)
        assertEquals("DotKt.Runtime.CompilerServices", a.Namespace)
        assertEquals("DotKt.Stdlib", a.Assembly.GetName().Name)
        val wide22 = wideParameterType("roundtrip.wide.WideFunctionTypesKt, RoundtripProducer", "param22")
        assertEquals("KFunc`23", wide22.Name)
        assertEquals("DotKt.Stdlib", wide22.Assembly.GetName().Name)
    }

    // No assembly but the stdlib may DEFINE a type in the canonical family — that is the whole ABI claim, and it is
    // only checkable on the emitted metadata (a referenced name sits in the same string heap as a defined one).
    @TestAttribute
    fun theConsumerDefinesNoWideDelegate() {
        val self = Type.GetType("WideFunctionTypeAnchor")!!.Assembly
        for (t in self.GetTypes()) {
            assertTrue(!t.Name.startsWith("KFunc`"))
            assertTrue(!t.Name.startsWith("KAction`"))
        }
    }
}

private fun wideParameterType(assemblyQualifiedType: String, method: String): Type {
    val owner = Type.GetType(assemblyQualifiedType)
    assertNotNull(owner)
    val m = owner!!.GetMethod(method)
    assertNotNull(m)
    return m!!.GetParameters()[0].ParameterType
}
