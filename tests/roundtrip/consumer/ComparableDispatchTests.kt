package roundtriptests.comparabledispatch

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.comparabledispatch.*

class ComparableDispatchTests {
    @TestAttribute
    fun comparableBoundDoesNotHideAnotherNullableBound() {
        val box = ComparisonBoundBox<Int?>(null)
        assertTrue(keepMixedBounds(1, box) === box)
    }

    @TestAttribute
    fun importedGenericComparisonAcceptsNativeEnums() {
        assertTrue(compareImported(ComparisonOrder.FIRST, ComparisonOrder.SECOND) < 0)
        assertTrue(compareImported(ComparisonOrder.SECOND, ComparisonOrder.FIRST) > 0)
        assertTrue(compareImported(ComparisonOrder.FIRST, ComparisonOrder.FIRST) == 0)
        assertTrue(maximumOrder() == ComparisonOrder.SECOND)
    }

    @TestAttribute
    fun importedInlineAndBoundReferenceAcceptNativeEnums() {
        assertTrue(compareInlineImported(ComparisonOrder.FIRST, ComparisonOrder.SECOND) < 0)
        assertTrue(compareReferenceImported(ComparisonOrder.FIRST, ComparisonOrder.SECOND) < 0)
    }

    @TestAttribute
    fun inheritedEnumComparisonUsesItsComparableContract() {
        assertEnumComparison(ComparisonOrder.FIRST, ComparisonOrder.SECOND)
        assertEnumComparison(ExplicitComparisonOrder.FIRST, ExplicitComparisonOrder.SECOND)
        val richFirst = RichComparisonOrder.FIRST
        val richSecond = RichComparisonOrder.SECOND
        assertEnumComparison(richFirst, richSecond)
        assertTrue((richFirst as System.IComparable).CompareTo(richSecond) == 77)
        assertTrue(DependentEnumComparisonBox<String, RichComparisonOrder>().compareBound(richFirst, richSecond) < 0)
    }

    private fun <T : Enum<T>> assertEnumComparison(first: T, second: T) {
        assertTrue(compareEnumBound(first, second) < 0)
        assertTrue(compareEnumBound(second, first) > 0)
        assertTrue(compareEnumBound(first, first) == 0)
        assertTrue(compareEnumBoundShifted("frame", first, second) < 0)
        assertTrue(compareEnumBoundInline(first, second) < 0)
        assertTrue(compareEnumBoundSpliced(first, second) { it } < 0)
        assertTrue(compareEnumBoundReference(first, second) < 0)
        assertTrue(compareEnumBoundUnbound(first, second) < 0)
        assertTrue(EnumBoundComparisonBox(first).compare(second) < 0)
        assertTrue(compareImported(first, second) < 0)
        assertTrue(maxOf(first, second) == second)
    }

    @TestAttribute
    fun enumBaseClassifierDoesNotBecomeAny() {
        val native: Any = ExplicitComparisonOrder.FIRST
        val rich: Any = RichComparisonOrder.FIRST
        assertTrue(enumView(native) == native)
        assertTrue(enumView(rich) === rich)
        assertTrue(enumView("not an enum") == null)
        assertTrue(enumView(null) == null)
        assertTrue(requireEnumView(native) == native)
        assertTrue(requireEnumView(rich) === rich)
        assertTrue(hasEnumView<Enum<*>>(native))
        assertTrue(hasEnumView<Enum<*>>(rich))
        assertTrue(!hasEnumView<Enum<*>>(Any()))
        assertTrue(!hasEnumView<Enum<*>>(1))
        assertTrue(!hasEnumView<Enum<*>>(null))
        assertTrue(hasEnumView<Array<Enum<*>>>(arrayOf(ExplicitComparisonOrder.FIRST)))
        assertTrue(hasEnumView<Array<Enum<*>>>(arrayOf(RichComparisonOrder.FIRST)))
        assertTrue(!hasEnumView<Array<Enum<*>>>(arrayOf("not an enum")))
        assertTrue(try { requireEnumView("not an enum"); false } catch (e: ClassCastException) { true })
    }

    @TestAttribute
    fun importedComparisonKeepsAuthoredAndPrimitiveSemantics() {
        val left = ComparisonValue(3)
        val right = ComparisonValue(8)
        assertTrue(compareImported(left, right) == -5)
        assertTrue(compareInlineImported(left, right) == -5)
        assertTrue(compareReferenceImported(left, right) == -5)
        assertTrue(compareImported(1, 2) < 0)
        assertTrue(compareImported("a", "b") < 0)
    }
}
