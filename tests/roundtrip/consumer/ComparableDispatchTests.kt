package roundtriptests.comparabledispatch

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.comparabledispatch.*
import GenericValueInterop.*

class ComparableDispatchTests {
    @TestAttribute
    fun erasedComparisonAcceptsGenericOnlyForeignValues() {
        val low = GenericOnlyComparable(3) as Comparable<GenericOnlyComparable>
        val high = GenericOnlyComparable(8) as Comparable<GenericOnlyComparable>
        assertTrue(low.compareTo(GenericOnlyComparable(8)) == -5)
        assertTrue(compareValues(low, high) == -5)
        assertTrue(compareValues(high, low) == 5)
        assertTrue(compareValues(low, low) == 0)
        assertTrue(compareValues(null, low) == -1)
        assertTrue(compareValues(low, null) == 1)
        assertTrue(compareValues<Comparable<GenericOnlyComparable>>(null, null) == 0)
        assertTrue(compareValuesBy(3, 8) { GenericOnlyComparable(it) as Comparable<GenericOnlyComparable> } == -5)
        val derived = DerivedComparable(1) as Comparable<GenericOnlyComparable>
        assertTrue(compareValues(derived, high) == -7)
        val valueLow = ValueComparable(2) as Comparable<ValueComparable>
        val valueHigh = ValueComparable(6) as Comparable<ValueComparable>
        assertTrue(compareValues(valueLow, valueHigh) == -4)
    }

    private fun erasedForeignCompare(receiver: Comparable<LeftComparison>, argument: ComparisonArgument): Int =
        compareValues(receiver, argument as Comparable<LeftComparison>)

    @TestAttribute
    fun erasedComparisonDoesNotDependOnInterfaceEnumerationOrder() {
        for (receiver in listOf(MultiComparable() as Comparable<LeftComparison>,
            ReversedMultiComparable() as Comparable<LeftComparison>)) {
            assertTrue(erasedForeignCompare(receiver, LeftArgument()) == 17)
            assertTrue(erasedForeignCompare(receiver, RightArgument()) == 29)
            assertTrue(try { erasedForeignCompare(receiver, BothArguments()); false }
                catch (e: ClassCastException) { e.message == "Ambiguous CLR comparison interfaces" })
            assertTrue(try { erasedForeignCompare(receiver, ComparisonArgument()); false }
                catch (e: ClassCastException) { e.message == "No applicable CLR comparison interface" })
        }
    }

    @TestAttribute
    fun erasedComparisonPreservesNativePrecedenceAndExceptions() {
        val dual = DualComparable() as Comparable<DualComparable>
        assertTrue(dual.compareTo(DualComparable()) == 53)
        assertTrue(compareValues(dual, dual) == 41)
        val throwing = ThrowingComparable()
        val view = throwing as Comparable<ThrowingComparable>
        assertTrue(try { compareValues(view, view); false }
            catch (failure: System.InvalidOperationException) { failure === throwing.Failure })
        assertTrue(compareValues(1, 2) < 0)
        assertTrue(compareValues("a", "b") < 0)
        assertTrue(compareValues(ComparisonValue(3), ComparisonValue(8)) == -5)
        assertTrue(compareValues(ComparisonOrder.FIRST, ComparisonOrder.SECOND) < 0)
    }

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
    fun importedConcreteEnumReferencesKeepTheirKotlinComparison() {
        val unbound: (EntryRichComparisonOrder, EntryRichComparisonOrder) -> Int = EntryRichComparisonOrder::compareTo
        val bound = EntryRichComparisonOrder.FIRST::compareTo
        assertTrue(unbound(EntryRichComparisonOrder.FIRST, EntryRichComparisonOrder.SECOND) == -1)
        assertTrue(unbound(EntryRichComparisonOrder.SECOND, EntryRichComparisonOrder.FIRST) == 1)
        assertTrue(unbound(EntryRichComparisonOrder.FIRST, EntryRichComparisonOrder.FIRST) == 0)
        assertTrue(bound(EntryRichComparisonOrder.SECOND) == -1)
        assertTrue(compareRichBoundReference(EntryRichComparisonOrder.FIRST, EntryRichComparisonOrder.SECOND) == -1)
        assertTrue(compareRichUnboundReference(EntryRichComparisonOrder.FIRST, EntryRichComparisonOrder.SECOND) == -1)
        assertEnumComparison(EntryRichComparisonOrder.FIRST, EntryRichComparisonOrder.SECOND)
        val native: (ExplicitComparisonOrder, ExplicitComparisonOrder) -> Int = ExplicitComparisonOrder::compareTo
        assertTrue(native(ExplicitComparisonOrder.FIRST, ExplicitComparisonOrder.SECOND) == -1)
        val nativeBound = ExplicitComparisonOrder.FIRST::compareTo
        assertTrue(nativeBound(ExplicitComparisonOrder.SECOND) == -1)
        val basicBound = ComparisonOrder.FIRST::compareTo
        assertTrue(basicBound(ComparisonOrder.SECOND) == -1)
        val clrControl: (RichComparisonOrder, RichComparisonOrder) -> Int = RichComparisonOrder::compareTo
        assertTrue(clrControl(RichComparisonOrder.FIRST, RichComparisonOrder.SECOND) == -1)
        assertTrue((RichComparisonOrder.FIRST as System.IComparable).CompareTo(RichComparisonOrder.SECOND) == 77)
        val authored: (ComparisonValue, ComparisonValue) -> Int = ComparisonValue::compareTo
        assertTrue(authored(ComparisonValue(3), ComparisonValue(8)) == -5)
    }

    @TestAttribute
    fun inheritedEnumComparisonUsesItsComparableContract() {
        assertEnumComparison(ComparisonOrder.FIRST, ComparisonOrder.SECOND)
        assertEnumComparison(ExplicitComparisonOrder.FIRST, ExplicitComparisonOrder.SECOND)
        assertEnumComparison(PlainRichComparisonOrder.FIRST, PlainRichComparisonOrder.SECOND)
        val richFirst = RichComparisonOrder.FIRST
        val richSecond = RichComparisonOrder.SECOND
        assertEnumComparison(richFirst, richSecond)
        assertTrue((richFirst as System.IComparable).CompareTo(richSecond) == 77)
        assertTrue(DependentEnumComparisonBox<String, RichComparisonOrder>().compareBound(richFirst, richSecond) < 0)
        assertIncompatibleEnumsThrow(ComparisonOrder.FIRST, ExplicitComparisonOrder.FIRST)
        assertIncompatibleEnumsThrow(PlainRichComparisonOrder.FIRST, RichComparisonOrder.FIRST)
        assertTrue((PlainRichComparisonOrder.FIRST as Any) is Comparable<*>)
        @Suppress("UNCHECKED_CAST")
        val plain = (PlainRichComparisonOrder.FIRST as Any) as Comparable<Any>
        assertTrue(plain.compareTo(PlainRichComparisonOrder.SECOND) < 0)
    }

    @Suppress("UNCHECKED_CAST")
    private fun assertIncompatibleEnumsThrow(left: Any, right: Any) {
        val comparison = left as Comparable<Any>
        assertTrue(try { comparison.compareTo(right); false } catch (e: ClassCastException) { true })
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
