import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.clr.byref
import roundtrip.clrrefparameters.incrementReferenced
import roundtrip.clrrefparameters.incrementReferencedInline
import roundtrip.clrrefparameters.swapReferenced
import roundtrip.clrrefparameters.updateReferencedInline
import roundtrip.clrrefparameters.updateReferencedThroughSameModuleInline
import roundtrip.clrrefparameters.ReferenceBox
import roundtrip.clrrefparameters.ReferenceWriter
import roundtrip.clrrefparameters.DerivedReferenceWriter
import roundtrip.clrrefparameters.OverridingReferenceWriter
import roundtrip.clrrefparameters.ScalarReferenceWriter
import roundtrip.clrrefparameters.sameModuleReferenceAlias
import GenericValueInterop.GenericFrameApi

private class ReferenceEvaluationProbe {
    var calls = 0
    fun next(): ReferenceBox<String> {
        calls += 1
        return ReferenceBox("unwritten")
    }
}

class ClrRefParameterTests {
    @TestAttribute
    fun genericOwnerReferencesPreserveLiveAliasesAndExceptionalWrites() {
        check(sameModuleReferenceAlias())
        var value = ReferenceBox("before")
        var live by byref(GenericFrameApi.Reference(byref(value)))
        val writer = ReferenceWriter<String, Int>()
        val replacement = ReferenceBox("after")
        check(writer.replace(byref(live), byref(live), replacement) === replacement)
        check(value === replacement && live === replacement)
        val next = ReferenceBox("next")
        check(writer.replaceAndReturn(byref(live), next, 42) == 42)
        check(value === next)
        try {
            writer.replaceAndThrow(byref(live), replacement)
            error("expected exception")
        } catch (expected: IllegalStateException) {
            check(value === replacement && live === replacement)
        }
        val implementation = OverridingReferenceWriter()
        val base: ReferenceWriter<String, Int> = implementation
        check(base.replace(byref(live), byref(live), next) === next)
        check(implementation.calls == 1 && value === next)
    }

    @TestAttribute
    fun genericOwnerReferenceContractsIgnoreUnrelatedTypeArguments() {
        var value = ReferenceBox("before")
        val writer = ReferenceWriter<String, Int>()
        @Suppress("UNCHECKED_CAST")
        val view = writer as ReferenceWriter<String, String>
        val replacement = ReferenceBox("after")
        check(view.replace(byref(value), byref(value), replacement) === replacement)
        check(view.replaceAndReturn(byref(value), replacement, "token") == "token")
        check(value === replacement)
        val inherited: ReferenceWriter<String, Int> = DerivedReferenceWriter<Int, String>()
        check(inherited.replace(byref(value), byref(value), replacement) === replacement)
        @Suppress("UNCHECKED_CAST")
        val wrong = ReferenceWriter<Int, Int>() as ReferenceWriter<String, Int>
        val evaluation = ReferenceEvaluationProbe()
        try {
            wrong.replace(byref(value), byref(value), evaluation.next())
            error("expected pointee mismatch")
        } catch (expected: ClassCastException) {
            check(evaluation.calls == 1 && value === replacement)
        }
    }

    @TestAttribute
    fun genericOwnerScalarReferencesRetainTheirPhysicalPointee() {
        var integer = 7
        check(ScalarReferenceWriter<String, Int>().replace(byref(integer), byref(integer), 42) == 42)
        check(integer == 42)
        var text = "before"
        check(ScalarReferenceWriter<Int, String>().replace(byref(text), byref(text), "after") == "after")
        check(text == "after")
    }

    @TestAttribute
    fun referencedKotlinManagedReferenceParametersRemainLive() {
        var value = 7
        assertEquals(12, incrementReferenced(byref(value), 5))
        assertEquals(12, value)
        assertEquals(14, incrementReferencedInline(byref(value), 2))
        assertEquals(14, value)
        assertEquals(42, updateReferencedInline(byref(value)) { it * 3 })
        assertEquals(42, value)
        assertEquals(46, updateReferencedThroughSameModuleInline(byref(value)))
        assertEquals(46, value)

        var first = "left"
        var second = "right"
        swapReferenced(byref(first), byref(second))
        assertEquals("right:left", "$first:$second")
    }
}
