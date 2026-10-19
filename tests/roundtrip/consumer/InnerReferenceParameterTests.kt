import NUnit.Framework.TestAttribute
import GenericValueInterop.GenericFrameApi
import kotlin.clr.ClrRef
import kotlin.clr.byref
import roundtrip.clrrefparameters.*

private class InnerReferenceEvaluation {
    var calls = 0
    fun next(): ReferenceBox<String> { calls += 1; return ReferenceBox("unwritten") }
}

private fun <A, T> selectedInnerReferenceOverload(outer: InnerReferenceOwner<A, T>, slot: ClrRef<ReferenceBox<T>>,
                                               replacement: ReferenceBox<T>): Int =
    outer.Overloaded(slot, replacement).selection

class InnerReferenceParameterTests {
    @TestAttribute
    fun constructorFactoriesPreserveNativeAliasesAndExceptionalWrites() {
        check(sameModuleInnerReferences())
        val outer: InnerReferenceOwner<Any, String> = InnerReferenceOwner<Int, String>()
        var value = ReferenceBox("before")
        var first by byref(GenericFrameApi.Reference(byref(value)))
        var second by byref(GenericFrameApi.Reference(byref(value)))
        val replacement = ReferenceBox("after")
        val writer = outer.Writer(byref(first), byref(second), replacement)
        check(value === replacement && first === replacement && second === replacement)
        check(writer.observed === replacement)
        val next = ReferenceBox("exceptional")
        try {
            outer.Writer(byref(first), byref(second), next, true)
            error("expected constructor exception")
        } catch (expected: IllegalStateException) {
            check(value === next && first === next && second === next)
        }
        check(innerReferenceForward(outer, byref(first), replacement) === replacement)
        check(first === replacement)
    }

    @TestAttribute
    fun constructorReferenceContractsIgnoreUnrelatedArgumentsAndEvaluateOperandsFirst() {
        val original = InnerReferenceOwner<Int, String>()
        @Suppress("UNCHECKED_CAST")
        val outer = original as InnerReferenceOwner<String, String>
        var value = ReferenceBox("before")
        val replacement = ReferenceBox("after")
        outer.Writer(byref(value), byref(value), replacement)
        check(value === replacement)
        @Suppress("UNCHECKED_CAST")
        val wrong = InnerReferenceOwner<Int, Int>() as InnerReferenceOwner<Any, String>
        val evaluation = InnerReferenceEvaluation()
        try {
            wrong.Writer(byref(value), byref(value), evaluation.next())
            error("expected pointee mismatch")
        } catch (expected: ClassCastException) {
            check(evaluation.calls == 1 && value === replacement)
        }
    }

    @TestAttribute
    fun constructorReferenceFactoriesRetainMethodPrimitiveAndConstraintFrames() {
        val outer: InnerReferenceOwner<Any, String> = InnerReferenceOwner<Int, String>()
        var value = ReferenceBox("before")
        var other = ReferenceBox(7)
        val replacement = ReferenceBox("after")
        val otherReplacement = ReferenceBox(42)
        outer.GenericWriter<Int>(byref(value), byref(other), replacement, otherReplacement)
        check(value === replacement && other === otherReplacement)
        check(selectedInnerReferenceOverload(outer, byref(value), replacement) == 1)
        check(sameModuleInnerReferenceOverload(outer, byref(value), replacement) == 1)
        val scalar: InnerReferenceOwner<Any, Int> = InnerReferenceOwner<String, Int>()
        var number = 7
        scalar.ScalarWriter(byref(number), 42)
        check(number == 42)
        val bounded: BoundedInnerReferenceOwner<Any, ReferenceBoundLeaf> =
            BoundedInnerReferenceOwner<Int, ReferenceBoundLeaf>()
        var constrained = ConstrainedReferenceBox(ReferenceBoundLeaf())
        val constrainedReplacement = ConstrainedReferenceBox(ReferenceBoundLeaf())
        bounded.Writer(byref(constrained), constrainedReplacement)
        check(constrained === constrainedReplacement)
    }
}
