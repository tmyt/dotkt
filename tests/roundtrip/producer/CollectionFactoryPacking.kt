package roundtrip.factorypacking

import ProjectedConstructionInterop.ObliviousBox

fun verifyCollectionFactoryPacking() {
    val direct = ObliviousBox<MutableList<out String>>(mutableListOf("direct", "second"))
    check(direct.Value.size == 2)
    check(direct.Value[0] == "direct")
    check(direct.Value[1] == "second")
    val source = arrayOf("forwarded", "tail")
    val forwarded = ObliviousBox<MutableList<out String>>(mutableListOf(*source))
    check(forwarded.Value.size == 2)
    check(forwarded.Value[0] == "forwarded")
    val mixed = ObliviousBox<MutableList<out String>>(mutableListOf("prefix", *source))
    check(mixed.Value.size == 3)
    check(mixed.Value[2] == "tail")
    check(ObliviousBox<MutableList<out String>>(mutableListOf()).Value.isEmpty())

    // The selected single-element overload must not unpack its array argument.
    val single = listOf(source)
    check(single.size == 1)
    check(single[0] === source)
    val literal = listOf(arrayOf("one", "two"))
    check(literal.size == 1)
    check(literal[0][1] == "two")
    val packedArrays = mutableListOf(source, source)
    check(packedArrays.size == 2)
    check(packedArrays[0] === source)
    check(packedArrays[1] === source)

    val set = setOf(*source)
    check(set.size == 2)
    check(set.contains("tail"))
    val pair = "key" to "value"
    check(mapOf(pair)["key"] == "value")
    check(mapOf("a" to "first", "b" to "second")["b"] == "second")
}
