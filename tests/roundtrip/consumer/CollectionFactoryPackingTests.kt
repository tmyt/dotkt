package roundtriptests.factorypacking

import NUnit.Framework.TestAttribute
import ProjectedConstructionInterop.ObliviousBox
import roundtrip.factorypacking.verifyCollectionFactoryPacking

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
    }
}
