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
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsNull as assertNull
import NvGen.Api

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
}
