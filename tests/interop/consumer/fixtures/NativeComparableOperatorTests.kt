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
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class NativeComparableOperatorTests {
    @Test
    fun dateTimeUsesItsComparableContractWithoutRenamingNativeMembers() {
        val first = DateTime(2020, 1, 1)
        val second = DateTime(2021, 1, 1)
        assertTrue(first < second)
        assertTrue(first <= second)
        assertTrue(second > first)
        assertTrue(second >= first)
        assertFalse(second < first)
        assertTrue(first.CompareTo(second) < 0)
    }

    @Test
    fun publicAndInheritedSlotsIgnoreUnrelatedSameNameOverloads() {
        val first = NativeOrder(2)
        val second = NativeOrder(9)
        assertTrue(first < second)
        assertTrue(InheritedOrder(3) < second)
        assertEquals(-7, first.CompareTo(second))
        assertEquals(1004, first.CompareTo(4))
        assertEquals(4004, first.compareTo(4))
    }

    @Test
    fun explicitAndInheritedImplementationsKeepTheirNonSelfContract() {
        assertTrue(ExplicitOrder(2) < OtherOrder(8))
        assertTrue(InheritedExplicitOrder(3) < OtherOrder(9))
        assertTrue(ExplicitOrder(2) > null)
        assertEquals(2004, ExplicitOrder(2).CompareTo(4))
        val bound: (OtherOrder?) -> Int = ExplicitOrder(2)::compareTo
        assertTrue(bound(OtherOrder(8)) < 0)
    }

    @Test
    fun constructedContractsSharingOneMethodDefinitionStayDistinct() {
        val value = DualOrder()
        assertTrue(value < "long")
        assertTrue(value > "x")
        assertTrue(value < 10)
        assertTrue(value > 2)
        assertEquals(3000, value.CompareTo("native API"))
    }

    @Test
    fun genericOwnerMappingAndValueReceiverAddressStayExact() {
        assertTrue(PermutedOrder<String, Int>(2) < Box<Int>(9))
        var value = MutableOrder(2)
        assertTrue(value < MutableOrder(9))
        assertEquals(1, value.Calls)
        assertTrue(value.CompareTo(MutableOrder(8)) < 0)
        assertEquals(2, value.Calls)
    }
}
