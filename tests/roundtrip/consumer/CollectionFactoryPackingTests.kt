package roundtriptests.factorypacking

import NUnit.Framework.TestAttribute
import ProjectedConstructionInterop.ObliviousBox
import roundtrip.factorypacking.verifyCollectionFactoryPacking
import roundtrip.factorypacking.copyForwardedArrayList

class CollectionFactoryPackingTests {
    @TestAttribute
    fun importedProducerKeepsFactoryPacking() {
        verifyCollectionFactoryPacking()
    }

    @TestAttribute
    fun consumerUsesSelectedFactoryOverload() {
        val value = ObliviousBox<MutableList<out String>>(mutableListOf("consumer"))
        check(value.Value.size == 1)
        check(value.Value[0] == "consumer")
        val source = arrayOf("array element")
        check(listOf(source)[0] === source)
        val forwarded = ObliviousBox<MutableList<out String>>(mutableListOf(*source))
        check(forwarded.Value[0] == "array element")
        val nullable = arrayOf<Int?>(null, 7)
        val copied = copyForwardedArrayList(nullable)
        nullable[1] = 99
        check(copied.size == 2 && copied[0] == null && copied[1] == 7)
        copied.add(8)
        copied[0] = 5
        check(copied[2] == 8 && nullable[0] == null && nullable[1] == 99)
        val native = copyForwardedArrayList(arrayOf(System.TimeSpan.FromTicks(9)))
        check(native.single().Ticks == 9L)
    }
}
