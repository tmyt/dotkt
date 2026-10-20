package roundtriptests.comparabledispatch

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.comparabledispatch.*
import GenericValueInterop.*
import kotlin.clr.byref

private class NativeComparisonAdapter : NativeComparisonBase() {
    override fun Apply(value: System.IComparable): System.IComparable = value
    override var Item: System.IComparable = DualComparable() as System.IComparable
}

class ComparableDispatchTests {
    @TestAttribute
    fun foreignComparableSelectorsKeepErasedResults() {
        val selector: (Int) -> Comparable<*>? = { GenericOnlyComparable(it) as Comparable<GenericOnlyComparable> }
        assertTrue(compareValuesBy(3, 8, selector) == -5)
        assertTrue(compareBy<Int> { GenericOnlyComparable(it) as Comparable<GenericOnlyComparable> }.compare(8, 3) == 5)
        assertTrue(compareValuesBy(3, 8, { 0 }, selector) == -5)
        assertTrue(compareBy<Int>({ 0 }, selector).compare(8, 3) == 5)
        assertTrue(comparisonKeepSelector(selector) === selector)
        assertTrue(compareValuesBy(3, 8, comparisonKeepSelector(selector)) == -5)
        assertTrue(listOf(8, 3, 5).sortedWith(compareBy(selector)).joinToString(",") == "3,5,8")
    }

    @TestAttribute
    fun comparableViewsPreserveIdentityAcrossSlotsAndDll() {
        val low = GenericOnlyComparable(3) as Comparable<GenericOnlyComparable>
        val high = GenericOnlyComparable(8) as Comparable<GenericOnlyComparable>
        val slot = ComparisonSlot(low)
        assertTrue(NativeComparisonApi.HasOpaqueSlot(slot))
        assertTrue(slot.value === low)
        assertTrue(comparisonKeep(slot.value) === low)
        assertTrue(comparisonNested(low).value === low)
        val array = comparisonArray(low)
        assertTrue(array[0] === low && array[1] == null)
        slot.value = high
        assertTrue(compareValues(low, slot.value) == -5)
        slot.value = null
        assertTrue(slot.value == null && comparisonKeep(null) == null)
        val broad = BroadComparison()
        val typed: Comparable<Int> = comparisonTypedKeep(broad)
        assertTrue(typed === broad && typed.compareTo(1) == 7)
        assertTrue(comparisonTypedNested(typed).value === broad)
    }

    @TestAttribute
    fun comparableClassifiersShareTheLogicalUnion() {
        val generic: Any = GenericOnlyComparable(3)
        val value: Any = ValueComparable(4)
        val multiple: Any = MultiComparable()
        var index = 0
        for (candidate in listOf(generic, value, multiple, 1, "value", DualComparable())) {
            assertTrue(candidate is Comparable<*>, "is instance " + index)
            assertTrue(candidate as Comparable<*> === candidate, "cast identity " + index)
            assertTrue(candidate as? Comparable<*> === candidate, "safe cast identity " + index)
            assertTrue(comparisonIs<Comparable<*>>(candidate), "reified classifier " + index)
            index++
        }
        assertTrue(Any() !is Comparable<*>, "non-comparable is")
        assertTrue((Any() as? Comparable<*>) == null, "non-comparable safe cast")
        assertTrue(!comparisonIs<Comparable<*>>(Any()), "non-comparable reified")
        assertTrue(!comparisonIs<Comparable<*>>(null), "null non-null reified")
        assertTrue(comparisonIs<Comparable<*>?>(null), "null nullable reified")
        val genericArray: Any = arrayOf(GenericOnlyComparable(3))
        assertTrue(comparisonIs<Array<Comparable<*>>>(genericArray), "generic-only reified array")
        assertTrue(!comparisonIs<Array<Comparable<*>>>(generic), "scalar is not an array")
        assertTrue(comparisonIs<Array<Array<Comparable<*>>>>(arrayOf(arrayOf(GenericOnlyComparable(3)))),
            "nested generic-only reified array")
        assertTrue(comparisonIs<Array<Comparable<GenericOnlyComparable>>>(genericArray), "typed generic-only array")
        assertTrue(comparisonCast<Array<Comparable<GenericOnlyComparable>>>(genericArray) === genericArray)
        assertTrue(comparisonSafeCast<Array<Comparable<GenericOnlyComparable>>>(genericArray) === genericArray)
        @Suppress("UNCHECKED_CAST")
        val directArray = genericArray as Array<Comparable<GenericOnlyComparable>>
        assertTrue(directArray === genericArray)
        val nativeEnum: Any = ExplicitComparisonOrder.FIRST
        assertTrue(comparisonIs<Comparable<ExplicitComparisonOrder>>(nativeEnum), "typed enum reified classifier")
        assertTrue(comparisonCast<Comparable<ExplicitComparisonOrder>>(nativeEnum) === nativeEnum,
            "typed enum reified cast identity")
        assertTrue(comparisonSafeCast<Comparable<ExplicitComparisonOrder>>(nativeEnum) === nativeEnum,
            "typed enum reified safe cast identity")
        assertTrue(comparisonSafeCast<Comparable<ExplicitComparisonOrder>>(Any()) == null)
        assertTrue(comparisonCast<Comparable<ExplicitComparisonOrder>?>(null) == null)
        assertTrue(try { comparisonCast<Comparable<ExplicitComparisonOrder>>(Any()); false }
            catch (e: ClassCastException) { true })
        assertTrue(comparisonSafeCast<Comparable<ExplicitComparisonOrder>>(null) == null)
        val absent: Any? = null
        assertTrue((absent as Comparable<*>?) == null)
        assertTrue((absent as? Comparable<*>) == null)
        assertTrue(try { absent as Comparable<*>; false } catch (e: NullPointerException) { true })
    }

    @TestAttribute
    @Suppress("UNCHECKED_CAST")
    fun comparableErasedMemberDispatchHandlesNullAndTypedViews() {
        val generic = GenericOnlyComparable(3) as Comparable<Any?>
        assertTrue(generic.compareTo(GenericOnlyComparable(8)) == -5)
        val operation = generic::compareTo
        assertTrue(operation(GenericOnlyComparable(8)) == -5)
        assertTrue((ValueComparable(2) as Comparable<Any?>).compareTo(ValueComparable(3)) == -1)
        assertTrue((NullAwareComparable() as Comparable<Any?>).compareTo(null) == 1)
        assertTrue((NullableValueComparable() as Comparable<Any?>).compareTo(null) == 17)
        assertTrue((NullableValueComparable() as Comparable<Any?>).compareTo(23) == 23)
        assertTrue((NullableValueComparable() as Comparable<Int?>).compareTo(null) == 17)
        assertTrue(try { (NonNullableValueComparable() as Comparable<Any?>).compareTo(null); false }
            catch (e: ClassCastException) { true })
        val multiple = MultiComparable()
        assertTrue((multiple as Comparable<LeftComparison?>).compareTo(null) == 17)
        assertTrue(try { (multiple as Comparable<Any?>).compareTo(null); false }
            catch (e: ClassCastException) { e.message == "Ambiguous CLR comparison interfaces" })
    }

    @TestAttribute
    fun nativeComparableDeclarationsAndManagedReferencesStayExact() {
        val initial = DualComparable() as System.IComparable
        val replacement = DualComparable() as System.IComparable
        var native: System.IComparable = NativeComparisonApi.Echo(initial)
        val adapter: NativeComparisonBase = NativeComparisonAdapter()
        assertTrue(NativeComparisonApi.HasNativeShape(adapter))
        assertTrue(adapter.Apply(native) === initial)
        adapter.Item = replacement
        assertTrue(adapter.Item === replacement)
        assertTrue(NativeComparisonApi.ReplaceAliased(byref(native), byref(native), replacement))
        assertTrue(native === replacement)
        var output: System.IComparable = initial
        NativeComparisonApi.CopyOut(byref(output), replacement)
        assertTrue(output === replacement)
        var live by byref(NativeComparisonApi.Address(byref(native)))
        live = initial
        assertTrue(native === initial && live === initial)
        native = replacement
        assertTrue(live === replacement)
    }

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
