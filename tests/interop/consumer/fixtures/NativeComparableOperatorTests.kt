import NativeComparison.Box
import NativeComparison.AnnotatedCrossAssemblyOrder
import NativeComparison.ComparisonToken
import NativeComparison.CrossAssemblyOrder
import NativeComparison.DualOrder
import NativeComparison.ExplicitOrder
import NativeComparison.InheritedExplicitOrder
import NativeComparison.InheritedOrder
import NativeComparison.MutableOrder
import NativeComparison.NativeOrder
import NativeComparison.LowercaseCollisionOrder
import NativeComparison.NullableCrossAssemblyOrder
import NativeComparison.NullableArgumentOrder
import NativeComparison.OtherOrder
import NativeComparison.PermutedOrder
import System.DateTime
import NUnit.Framework.TestAttribute as Test

private class KotlinNativeOrder(value: Int) : NativeOrder(value) {
    override fun CompareTo(other: NativeOrder?): Int = super.CompareTo(other) - 20
}
private class KotlinExplicitOrder(value: Int) : ExplicitOrder(value)
private class KotlinClosedOrder(value: Int) : PermutedOrder<String, Int>(value)
private class KotlinPermutedOrder<A, B>(value: Int) : PermutedOrder<B, A>(value)
private class GenericComparisonCaller<T> {
    fun compare(left: KotlinClosedOrder, right: Box<Int>): Boolean = left < right
}
private class ComparisonHolder {
    companion object : NativeOrder(2)
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
        val companionComparison: (NativeOrder?) -> Int = ComparisonHolder.Companion::compareTo
        check(companionComparison(NativeOrder(9)) < 0)
        val collision = LowercaseCollisionOrder()
        check(collision.compareTo(1) == 1001)
        check(collision.CompareTo(1) == -6)
        check(collision < 1)
    }

    @Test
    fun explicitAndInheritedImplementationsKeepTheirNonSelfContract() {
        check(ExplicitOrder(2) < OtherOrder(8))
        check(InheritedExplicitOrder(3) < OtherOrder(9))
        check(ExplicitOrder(2) > null)
        check(ExplicitOrder(2).CompareTo(4) == 2004)
        val bound: (OtherOrder?) -> Int = ExplicitOrder(2)::compareTo
        check(bound(OtherOrder(8)) < 0)
        val unbound: (KotlinExplicitOrder, OtherOrder?) -> Int = KotlinExplicitOrder::compareTo
        check(unbound(KotlinExplicitOrder(2), OtherOrder(8)) < 0)
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
        check(NullableArgumentOrder<String>(2) < Box<String?>(9))
        check(KotlinClosedOrder(2) < Box<Int>(9))
        check(KotlinPermutedOrder<Int, String>(2) < Box<Int>(9))
        check(GenericComparisonCaller<OtherOrder>().compare(KotlinClosedOrder(2), Box<Int>(9)))
        val token = ComparisonToken(9)
        check(CrossAssemblyOrder(2) < token)
        check(AnnotatedCrossAssemblyOrder(2) < token)
        check(NullableCrossAssemblyOrder(2) < token)
        check(NullableCrossAssemblyOrder(2) > null)
        var value = MutableOrder(2)
        check(value < MutableOrder(9))
        check(value.Calls == 1)
        check(value.CompareTo(MutableOrder(8)) < 0)
        check(value.Calls == 2)
    }
}
