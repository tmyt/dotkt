// General #367 regression: this consumes a separate, ordinary C# NRT assembly. Nothing here names Console or any
// BCL owner, so the behavior can only come from dll2klib's declaration-shape rule.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NrtParams.Api
import NrtParams.CtorProbe
import NrtParams.FinalApi
import NrtParams.VirtualApi
import NrtParams.*

private class NrtParamsDerived : VirtualApi() {
    override fun Pick(value: String?): String = "override:" + (value ?: "<null>")
}

class NrtParamsOverloadTests {
    @TestAttribute
    fun nrtOnlySpecificityDoesNotInvertFixedAndParamsOverloads() {
        assertEquals("fixed:x", Api.Pick("x"))
        val maybe: String? = "n"
        assertEquals("fixed:n", Api.Pick(maybe))
        assertEquals("fixed:named", Api.Pick(value = "named"))
        assertEquals("params:1", Api.Pick("x", 1))
        assertEquals("params:0", Api.Pick("x", *emptyArray<Any?>()))
        assertEquals("params:0", Api.Pick(format = "x", args = emptyArray<Any?>()))

        // A real CLR Object-vs-String prefix difference is not an NRT-only inversion and must remain untouched.
        assertEquals("params:0", Api.Different("x"))

        assertEquals("fixed:value", Api.Generic<String>("x"))
        assertEquals("fixed:value", Api.Generic("x"))
        val genericMaybe: String? = null
        assertEquals("fixed:<null>", Api.Generic<String>(genericMaybe))
        assertEquals("fixed:x:2", Api.Pair("x", 2))
        assertEquals("params:1", Api.Pair("x", 2, 3))

        assertEquals("fixed:x", FinalApi().Pick("x"))
        assertEquals("fixed:x", CtorProbe("x").Which)
        assertEquals("params:1", CtorProbe("x", 1).Which)

        assertEquals("fixed:x", "x".Pick())
        assertEquals("params:1", "x".Pick(1))

        val staticReference: (String) -> String = Api::Pick
        assertEquals("fixed:ref", staticReference("ref"))
        val constructorReference: (String) -> CtorProbe = ::CtorProbe
        assertEquals("fixed:ctor-ref", constructorReference("ctor-ref").Which)

        val derived = NrtParamsDerived()
        val base: VirtualApi = derived
        assertEquals("override:x", derived.Pick("x"))
        assertEquals("override:x", base.Pick("x"))
        assertEquals("params:1", base.Pick("x", 1))
    }
}
