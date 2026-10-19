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
import roundtrip.clrrefparameters.ReferenceInterface
import roundtrip.clrrefparameters.StringReferenceInterface
import roundtrip.clrrefparameters.ScalarReferenceWriter
import roundtrip.clrrefparameters.sameModuleReferenceAlias
import roundtrip.clrrefparameters.CovariantReferenceWriter
import roundtrip.clrrefparameters.anyReferenceView
import roundtrip.clrrefparameters.ReferenceBound
import roundtrip.clrrefparameters.ReferenceItem
import roundtrip.clrrefparameters.BoundReferenceWriter
import roundtrip.clrrefparameters.ReferenceBoundBase
import roundtrip.clrrefparameters.ReferenceBoundMiddle
import roundtrip.clrrefparameters.ReferenceBoundLeaf
import roundtrip.clrrefparameters.ConstrainedReferenceBox
import roundtrip.clrrefparameters.ConstrainedReferenceWriter
import GenericValueInterop.GenericFrameApi
import kotlin.clr.ClrRef

private class ConsumerReferenceWriter : ReferenceWriter<String, Int>() {
    var calls = 0
    override fun replace(first: ClrRef<ReferenceBox<String>>, second: ClrRef<ReferenceBox<String>>,
                         value: ReferenceBox<String>): ReferenceBox<String> {
        calls += 1
        first.value = value
        return second.value
    }
}

private class LocalReferenceWriter<T> {
    fun replace(slot: ClrRef<ReferenceBox<T>>, value: ReferenceBox<T>) { slot.value = value }
}

private class ReferenceEvaluationProbe {
    var calls = 0
    fun next(): ReferenceBox<String> {
        calls += 1
        return ReferenceBox("unwritten")
    }
}

class ClrRefParameterTests {
    @TestAttribute
    fun referenceContractsRetainOnlyPhysicalPointeeDependencies() {
        @Suppress("UNCHECKED_CAST")
        val covariant = CovariantReferenceWriter<Int>() as CovariantReferenceWriter<Any>
        var view = anyReferenceView()
        val originalView = view
        covariant.keep(byref(view))
        check(view === originalView && view.value == 1)

        @Suppress("UNCHECKED_CAST")
        val bound = BoundReferenceWriter<ReferenceBound, ReferenceItem>() as BoundReferenceWriter<Any, ReferenceItem>
        var item = ReferenceItem()
        val originalItem = item
        bound.keep(byref(item))
        check(item === originalItem)

        @Suppress("UNCHECKED_CAST")
        val constrained = ConstrainedReferenceWriter<ReferenceBoundMiddle, ReferenceBoundLeaf>()
            as ConstrainedReferenceWriter<ReferenceBoundBase, ReferenceBoundLeaf>
        var box = ConstrainedReferenceBox(ReferenceBoundLeaf())
        val originalBox = box
        constrained.keep(byref(box))
        check(box === originalBox)
    }

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
        val consumerImplementation = ConsumerReferenceWriter()
        val consumerBase: ReferenceWriter<String, Int> = consumerImplementation
        check(consumerBase.replace(byref(live), byref(live), replacement) === replacement)
        check(consumerImplementation.calls == 1 && value === replacement)
        val defaultWriter: ReferenceInterface<String> = StringReferenceInterface()
        check(defaultWriter.replace(byref(live), byref(live), next) === next)
        check(value === next)
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
        @Suppress("UNCHECKED_CAST")
        val localWrong = LocalReferenceWriter<Int>() as LocalReferenceWriter<String>
        try {
            localWrong.replace(byref(value), evaluation.next())
            error("expected local pointee mismatch")
        } catch (expected: ClassCastException) {
            check(evaluation.calls == 2 && value === replacement)
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
