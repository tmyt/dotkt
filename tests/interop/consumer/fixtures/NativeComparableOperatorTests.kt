import NativeComparison.Box
import NativeComparison.DualOrder
import NativeComparison.ExplicitOrder
import NativeComparison.InheritedExplicitOrder
import NativeComparison.InheritedOrder
import NativeComparison.MutableOrder
import NativeComparison.NativeOrder
import NativeComparison.OtherOrder
import NativeComparison.PermutedOrder
import System.DateTime
import NUnit.Framework.TestAttribute as Test

private class KotlinNativeOrder(value: Int) : NativeOrder(value) {
    override fun CompareTo(other: NativeOrder?): Int = super.CompareTo(other) - 20
}

class NativeComparableOperatorTests {
    @Test
    fun dateTimeUsesItsComparableContractWithoutRenamingNativeMembers() {
        val first = DateTime(2020, 1, 1)
        val second = DateTime(2021, 1, 1)
        check(first < second)
        check(first <= second)
        check(second > first)
        check(second >= first)
        check(!(second < first))
        check(first.CompareTo(second) < 0)
    }

    @Test
    fun publicAndInheritedSlotsIgnoreUnrelatedSameNameOverloads() {
        val first = NativeOrder(2)
        val second = NativeOrder(9)
        check(first < second)
        check(InheritedOrder(3) < second)
        check(first.CompareTo(second) == -7)
        check(first.CompareTo(4) == 1004)
        check(first.compareTo(4) == 4004)
        // The inherited view must dispatch to the original CLR virtual slot.
        check(KotlinNativeOrder(10) < NativeOrder(2))
    }

    @Test
    fun explicitAndInheritedImplementationsKeepTheirNonSelfContract() {
        check(ExplicitOrder(2) < OtherOrder(8))
        check(InheritedExplicitOrder(3) < OtherOrder(9))
        check(ExplicitOrder(2) > null)
        check(ExplicitOrder(2).CompareTo(4) == 2004)
        val bound: (OtherOrder?) -> Int = ExplicitOrder(2)::compareTo
        check(bound(OtherOrder(8)) < 0)
    }

    @Test
    fun constructedContractsSharingOneMethodDefinitionStayDistinct() {
        val value = DualOrder()
        check(value < "long")
        check(value > "x")
        check(value < 10)
        check(value > 2)
        check(value.CompareTo("native API") == 3000)
    }

    @Test
    fun genericOwnerMappingAndValueReceiverAddressStayExact() {
        check(PermutedOrder<String, Int>(2) < Box<Int>(9))
        var value = MutableOrder(2)
        check(value < MutableOrder(9))
        check(value.Calls == 1)
        check(value.CompareTo(MutableOrder(8)) < 0)
        check(value.Calls == 2)
    }
}
