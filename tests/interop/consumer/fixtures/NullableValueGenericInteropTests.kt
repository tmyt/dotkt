// #86 — the positions a nullable VALUE type crosses the .NET boundary unchanged, driven at run time.
//
// The erasure moves a possibly-value `X?` to `System.Object` in a reified ARGUMENT, and a refusal guards the .NET
// declarations no Kotlin expression can then inhabit (`List<int?>`, pinned as
// tests/compile-fail/ForeignNullableGenericCrossing.kt). This battery is the other half of that boundary: the
// shapes Kotlin inhabits EXACTLY, which a refusal one position too wide would make uncallable. A compile-fail case
// cannot show these still work — only calling them can, so they are asserted here by value.
//
// The `Func<int?, string>` case is the one the position rule turns on: a delegate PARAMETER keeps its concrete
// `Nullable<int32>`, so the lifted Kotlin lambda declares the same slot and the delegate is well-formed. A copy of
// the position walk that called a delegate parameter an argument refused this exact member.
//
// NOT driven here: an `out int?` referent, which is a slot in the same rule. `ClrRef` parameters have a separate,
// separately-tracked defect (see ByRefParameterTests' note), so a case there would assert that instead of this.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsNull as assertNull
import NvGen.Api
import NvGen.GenFac
import NvGen.INotObliged
import NvGen.NotObligedBase
import NvGen.ImageSibling
import NvGen.NetFillsIt
import NvGen.OverloadBase
import NvGen.ValueImageBase
import System.Collections.Generic.List as NetList

// THE OTHER HALF OF THE IMPLEMENTING-POSITION REFUSAL: what an author may still write next to an uninhabitable
// slot. These three declarations ARE the assertion — a refusal keyed on "does this type inherit such a slot"
// rejected all three at compile time, and a rejected program has no test to run. They compile here, so the
// obligation is decided per type and per slot.
//
// An INTERFACE and an ABSTRACT class inherit the obligation without discharging it: neither is instantiable and
// neither emits a body, so there is no position for the slot's uninhabitable parameter to appear in. Kotlin
// re-declares the inherited member on the deriving interface as a fake override, which states the slot again and
// still fills nothing.
interface KotlinInterfaceOverForeignSlot : INotObliged

abstract class KotlinAbstractOverForeignSlot : NotObligedBase()

// And a CONCRETE class may override a DIFFERENT member of the same overload set. `Take(List<int?>)` and
// `Take(string)` share a name and a parameter count, so a refusal that matched on those two facts refused this
// class for a member it never mentions. The crossing overload keeps its .NET body.
class KotlinOverloadSibling : OverloadBase() {
    override fun Take(s: String): String = "kt:" + s
}

// A .NET base that ALREADY fills the crossing slot discharges it for everything below, and it does so here through
// an EXPLICIT implementation — whose CLR member name is qualified with the interface, so it is not the interface's
// member name and the abstract declaration looked undischarged.
class KotlinBelowNetImplementation : NetFillsIt()

// And where the crossing slot's ERASED IMAGE is a signature a sibling states outright, the override belongs to the
// sibling. `Take(List<int?>)` images to `Take(List<object>)`, which `ImageSibling` also really declares.
class KotlinImageSiblingOverride : ImageSibling() {
    override fun Take(ys: NetList<Any?>): String = "kt-o"
}

// AND THE IMAGE MAY BE A SLOT OF A DIFFERENT SUPERTYPE ENTIRELY. `List<Boolean?>` erases to the same `List<object>`
// as the foreign `Take(List<int?>)`, and — unlike the `List<Any?>` sibling above — it is a possibly-VALUE argument,
// so the erasure records its pre-erasure type just as it records the crossing's. A refusal that read only whether
// SOME record was there could not tell one from the other and refused this class for a slot it never mentions. The
// record says `List<Boolean?>`, the foreign slot says `List<int?>`, and they are two slots.
interface KotlinBoolListSink {
    fun Take(zs: NetList<Boolean?>): String
}

class KotlinValueImageSibling : ValueImageBase(), KotlinBoolListSink {
    override fun Take(zs: NetList<Boolean?>): String = "kt-bool"
}

class NullableValueGenericInteropTests {
    @TestAttribute
    fun directNullableValueSlotsCrossUnchanged() {
        assertEquals(7, Api.OrElse(null, 7))            // 7    a direct int? PARAMETER takes a Kotlin null
        assertEquals(3, Api.OrElse(3, 7))               // 3
        assertEquals(2, Api.Halve(5))                   // 2    a direct int? RETURN reads back as Int?
        assertNull(Api.Halve(null))                     // null
    }

    @TestAttribute
    fun nullableValueDelegateParameterCrossesUnchanged() {
        // A `Func<int?, string>` PARAMETER: the lambda's own slot is the same `Nullable<int32>`, so the delegate is
        // well-formed and the .NET side can pass either state through it.
        assertEquals("5", Api.Describe(5) { v -> v?.toString() ?: "none" })       // 5
        assertEquals("none", Api.Describe(null) { v -> v?.toString() ?: "none" }) // none
        assertEquals("4|none", Api.DescribeTwice(4) { v -> v?.toString() ?: "none" })  // 4|none
    }

    // The overload sibling RUNS, and it dispatches through the CLR slot it overrides — so the class did not merely
    // compile, it LOADED, with its one slot filled and the crossing one left to the .NET body. (Calling that other
    // overload from Kotlin is the call-side crossing and is refused; it is pinned in tests/compile-fail.)
    @TestAttribute
    fun overridingTheSiblingOfACrossingOverloadRuns() {
        val o: OverloadBase = KotlinOverloadSibling()
        assertEquals("kt:a", o.Take("a"))               // kt:a   the Kotlin override, through the base slot
        assertEquals("net:b", OverloadBase().Take("b")) // net:b  the base's own body, untouched
    }

    // A GENERIC overload set whose OTHER member returns an uninhabitable slot. The declared return is resolved
    // through this call's own `memberSig`, so the crossing one is refused (tests/compile-fail) and this one is not.
    @TestAttribute
    fun siblingOfACrossingGenericOverloadStillBinds() {
        assertEquals("gen:a", GenFac.Pick<Int>("a"))    // gen:a
    }

    // Both types LOAD and dispatch, which is what says the obligation was placed on the right member: a class below
    // an explicit .NET implementation adds no slot of its own, and an override of the image-sibling reaches the CLR
    // slot that sibling declares rather than shadowing it.
    @TestAttribute
    fun aDischargedSlotAndAnImageSiblingAreNotThisTypesObligation() {
        assertEquals("filled", KotlinBelowNetImplementation().Describe())   // filled
        val o: ImageSibling = KotlinImageSiblingOverride()
        assertEquals("kt-o", o.Take(NetList<Any?>()))   // kt-o   through the List<object> slot the sibling declares
    }

    // The body whose ERASED IMAGE coincides with a foreign crossing slot while it fills a DotKt one. It compiles,
    // the type LOADS, and the call dispatches through the Kotlin interface's slot — the foreign `Take(List<int?>)`
    // keeps its own .NET body and was never this type's to fill.
    @TestAttribute
    fun aBodyRecordedForAnotherSlotIsNotTheForeignSlotsImplementation() {
        val k: KotlinBoolListSink = KotlinValueImageSibling()
        assertEquals("kt-bool", k.Take(NetList<Boolean?>()))   // kt-bool   through the DotKt interface's own slot
    }
}
