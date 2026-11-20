package roundtriptests.factorypacking

import NUnit.Framework.TestAttribute
import ProjectedConstructionInterop.ObliviousBox
import roundtrip.factorypacking.*
import ProjectedConstructionInterop.NativeListShape

class CollectionFactoryPackingTests {
    @TestAttribute
    fun kotlinListPreservesErasedElementsAndNativeArrayIdentity() {
        val readers = packElement(reader(true))
        readers.add(reader(false))
        check((readers[0] as Any) === NumberReader && (readers[1] as Any) === TextReader)
        check(listOf(true, false).map { reader(it) }.map { it.label() }.joinToString() == "number, text")
        check(mutableListOf(reader(false))[0].label() == "text")
        check(literalElement(reader(true))[0].label() == "number")
        check(NativeListShape.IsExact<Reader<Any?>>(System.Collections.Generic.List<Reader<Any?>>()))
        check(!NativeListShape.IsExact<Reader<Any?>>(readers))
        val ints = arrayOf(1)
        check(NativeListShape.IsExact<Array<Int>>(packElement(ints)))
        check(NativeListShape.IsExact<Array<Int>>(forwardArray(ints)))
        check(NativeListShape.IsExact<Array<Int>>(ArrayList<Array<Int>>()))
        val virtual: ListPacker = VirtualListPacker
        check(NativeListShape.IsExact<Array<Int>>(virtual.pack(ints)))
        check(NativeListShape.IsExact<Int>(virtual.pack(1)))
        @Suppress("UNCHECKED_CAST")
        val fromVirtual = virtual.pack(reader(true)) as ArrayList<Reader<Any?>>
        check(fromVirtual[0].label() == "number")
    }
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
