package roundtrip.factorypacking

import ProjectedConstructionInterop.ObliviousBox

private fun <T> packedGenericValue(value: T): T =
    ObliviousBox<MutableList<out T>>(mutableListOf(value)).Value[0]

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
    val singleSet = setOf(source)
    check(singleSet.size == 1)
    check(singleSet.first() === source)
    val empty = emptyArray<String>()
    check(listOf(*empty).isEmpty())
    check(setOf(*empty).isEmpty())
    check(mutableListOf(*empty).isEmpty())
    check(arrayListOf(*source)[1] == "tail")
    check(hashSetOf(*source).contains("forwarded"))
    val pair = "key" to "value"
    check(mapOf(pair)["key"] == "value")
    check(mapOf("a" to "first", "b" to "second")["b"] == "second")
    val pairs = arrayOf("a" to "first", "b" to "second")
    check(mapOf(*pairs)["b"] == "second")
    check(hashMapOf(*pairs)["a"] == "first")
    check(mutableMapOf(*pairs)["b"] == "second")
    check(mapOf(*emptyArray<Pair<String, String>>()).isEmpty())

    val nullable = ObliviousBox<MutableList<out Int?>>(mutableListOf(7, null))
    check(nullable.Value.size == 2)
    check(nullable.Value[0] == 7)
    check(nullable.Value[1] == null)
    check(packedGenericValue("generic") == "generic")
    check(packedGenericValue(9) == 9)
    check(packedGenericValue<Int?>(null) == null)

    var order = ""
    fun mark(value: String): String {
        order += value
        return value
    }
    val ordered = ObliviousBox<MutableList<out String>>(
        mutableListOf(mark("a"), *arrayOf(mark("b")), mark("c")))
    check(order == "abc")
    check(ordered.Value.size == 3)
    check(ordered.Value[0] == "a")
    check(ordered.Value[2] == "c")
}
