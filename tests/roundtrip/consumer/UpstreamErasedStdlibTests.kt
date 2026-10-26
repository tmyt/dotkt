import NUnit.Framework.TestAttribute
import GenericValueInterop.DualComparable
import GenericValueInterop.GenericOnlyComparable
import GenericValueInterop.ThrowingComparable
import GenericValueInterop.UpstreamMapCopies
import GenericValueInterop.ValueComparable
import roundtrip.upstreamerasedstdlib.*

class UpstreamErasedStdlibTests {
    @TestAttribute
    fun emptyMapRetainsGenericAndReadonlyContractsAcrossDlls() {
        val integers: Map<Int, String> = emptyMapInLibrary()
        val references: Map<String, List<Int>> = emptyMapInLibrary()
        val nullable: Map<Int?, String?> = emptyMapInLibrary()
        check(integers.isEmpty() && integers.size == 0 && integers[1] == null)
        check(references["missing"] == null && references.keys.isEmpty())
        check(nullable[null] == null && !nullable.containsKey(null) && !nullable.containsValue(null))
        check(nullable.entries.isEmpty() && nullable.values.isEmpty())
        check(integers == emptyMap<Int, String>() && integers.hashCode() == 0)
        check(integers.toString() == "{}")
        check((integers as Any) is Map<*, *> && (integers as Any) !is MutableMap<*, *>)
        check(UpstreamMapCopies.AddToEmpty(integers.toClrDictionary()) == 1)
        check(integers.isEmpty())
        check(hashMapOf(null to "null", 1 to "one")[null] == "null")
        val authored = authoredEmptyMapInLibrary<Int, String>()
        check(authored.entries.isEmpty() && authored.keys.isEmpty() && authored.values.isEmpty())
        check(authored.toClrDictionary().Count == 0)
        val native = System.Collections.Generic.Dictionary<Int, String>()
        native.Add(1, "one")
        val view = (native as Any) as Map<Int, String>
        check(view.entries.single().key == 1 && view.entries.single().value == "one")
        val exact: System.Collections.Generic.IReadOnlyCollection<Int> = UpstreamMapCopies.NativeValues()
        check(exact.Count == 2 && UpstreamMapCopies.ReadNativeValues(exact) == 2)
        check(UpstreamMapCopies.ReadNativeNestedValues(UpstreamMapCopies.NativeNestedValues()) == 1)
        check(UpstreamMapCopies.ReadNativeArray(UpstreamMapCopies.NativeArray()) == 2)
        check(UpstreamMapCopies.ReadNativeDelegate(UpstreamMapCopies.NativeDelegate()) == 2)
        check(UpstreamMapCopies.NativeSignaturesRemainExact())
    }

    @TestAttribute
    fun naturalAndReverseComparatorsRetainTypedViewsAcrossDlls() {
        val integers: Comparator<Int> = naturalOrderInLibrary()
        val strings: Comparator<String> = naturalOrderInLibrary()
        val values: Comparator<OrderedValue> = naturalOrderInLibrary()
        val entries: Comparator<OrderedEntry> = naturalOrderInLibrary()
        check(integers.compare(1, 3) < 0 && strings.compare("a", "z") < 0)
        check(values.compare(OrderedValue(2), OrderedValue(7)) == -5)
        check(entries.compare(OrderedEntry.FIRST, OrderedEntry.SECOND) < 0)
        check(reverseOrderInLibrary<Int>().compare(1, 3) > 0)
        check(reverseOrderInLibrary<String>().compare("a", "z") > 0)
        check(reverseOrderInLibrary<OrderedValue>().compare(OrderedValue(2), OrderedValue(7)) == 5)
        check(reverseOrderInLibrary<OrderedEntry>().compare(OrderedEntry.FIRST, OrderedEntry.SECOND) > 0)
    }

    @TestAttribute
    fun restoredComparatorReversalAndNullableWrappersCompose() {
        val natural: Comparator<Int> = naturalOrderInLibrary()
        val reverse = reversedInLibrary(natural)
        check(reverse.compare(1, 3) > 0)
        check(reversedInLibrary(reverse).compare(1, 3) < 0)
        check(nullsFirst(natural).compare(null, 1) < 0)
        check(nullsLast(reverse).compare(1, null) < 0)
        check(listOf(3, 1, 2).sortedWith(natural).joinToString(",") == "1,2,3")
        check(listOf("a", "z", "m").sortedWith(reverseOrderInLibrary()).joinToString(",") == "z,m,a")
        check(directJoinedValue<Int>(true).select(42) == 1)
        check(directJoinedValue<Int>(false).select(42) == 2)
        check(nestedJoinedValue<String>(0).select("value") == 1)
        check(nestedJoinedValue<String>(1).select("value") == 2)
        check(nestedJoinedValue<String>(2).select("value") == 2)
        val concrete = concreteJoinedValue<Int>(false)
        check(concrete.select(42) == 2)
        check(UpstreamMapCopies.HasConstructedReturn(concrete, "concreteJoinedValue"))
        check(nullableJoinedValue<Int>(true)?.select(42) == 1)
        check(nullableJoinedValue<Int>(false) == null)
        check(throwingJoinedValue<Int>(true).select(42) == 1)
        check(try { throwingJoinedValue<Int>(false); false }
            catch (failure: IllegalStateException) { failure.message == "missing joined value" })
        val localUse: JoinedLocalUse = DerivedJoinedLocalUse()
        check(localUse.select(true) == 1 && localUse.select(false) == 2)
        check(JoinedLocalUse().select(true) == 1 && JoinedLocalUse().select(false) == 2)
        val overriding: JoinedValueBase<Int> = JoinedValueDerived()
        check(overriding.select(true).select(42) == 2 && overriding.select(false).select(42) == 2)
        val carriers: JoinedCarrierBase<Int> = JoinedCarrierDerived()
        check(carriers.select().select(42) == 1)
        val arrays: JoinedArrayBase<Int> = JoinedArrayDerived()
        check(arrays.values(true)[0].select(42) == 2)
        check(UpstreamMapCopies.OverrideReturnMatches(arrays, "values"))
    }

    @TestAttribute
    fun restoredComparableCastDispatchesFromGenericLibraryCode() {
        check(compareValuesInLibrary<Int>(null, 1) < 0)
        check(compareValuesInLibrary<Int>(1, null) > 0)
        check(compareValuesInLibrary<Int>(null, null) == 0)
        check(compareValuesInLibrary(1, 3) < 0)
        check(compareValuesInLibrary("a", "z") < 0)
        check(compareValuesInLibrary(OrderedValue(2), OrderedValue(7)) == -5)
        check(compareValuesInLibrary(OrderedEntry.FIRST, OrderedEntry.SECOND) < 0)
    }

    @TestAttribute
    fun restoredComparisonRetainsForeignExplicitSlotsAndExceptions() {
        val low = GenericOnlyComparable(2) as Comparable<GenericOnlyComparable>
        val high = GenericOnlyComparable(7) as Comparable<GenericOnlyComparable>
        check(compareValuesInLibrary(low, high) == -5)
        val valueLow = ValueComparable(3) as Comparable<ValueComparable>
        val valueHigh = ValueComparable(8) as Comparable<ValueComparable>
        check(compareValuesInLibrary(valueLow, valueHigh) == -5)
        val dual = DualComparable() as Comparable<DualComparable>
        check(dual.compareTo(DualComparable()) == 53)
        check(compareValuesInLibrary(dual, dual) == 41)
        val throwing = ThrowingComparable()
        val view = throwing as Comparable<ThrowingComparable>
        check(try { compareValuesInLibrary(view, view); false }
            catch (failure: System.InvalidOperationException) { failure === throwing.Failure })
    }
}
