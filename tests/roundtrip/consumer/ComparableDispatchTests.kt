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
        val first = ComparisonOrder.FIRST
        val second = ComparisonOrder.SECOND
        assertTrue(compareEnumBound(first, second) < 0)
        assertTrue(compareEnumBound(second, first) > 0)
        assertTrue(compareEnumBound(first, first) == 0)
        assertTrue(compareEnumBoundShifted("frame", first, second) < 0)
        assertTrue(compareEnumBoundInline(first, second) < 0)
        assertTrue(compareEnumBoundReference(first, second) < 0)
        assertTrue(compareEnumBoundUnbound(first, second) < 0)
        assertTrue(EnumBoundComparisonBox(first).compare(second) < 0)
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
