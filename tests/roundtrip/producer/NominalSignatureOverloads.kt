package roundtrip.nominaloverloads

class NominalArrayConstructor {
    val chosen: Int
    constructor(values: Array<Int>) { chosen = 1; check(values.size == 2) }
    constructor(values: IntArray) { chosen = 2; check(values.size == 2) }
}

class NominalNullableConstructor {
    val chosen: Int
    constructor(ints: List<Int?>) { chosen = 1; check(ints.size == 2 && ints[1] == null) }
    constructor(longs: List<Long?>) { chosen = 2; check(longs.size == 2 && longs[1] == null) }
}

@kotlin.clr.ClrName("samePhysicalName")
fun Collection<Int>.left(): Int = 1

@kotlin.clr.ClrName("samePhysicalName")
fun Set<Int>.right(): Int = 2

fun Collection<Int>.ordinaryPhysicalName(): Int = 3
fun Set<Int>.ordinaryPhysicalName(): Int = 4

val Collection<Int>.firstProperty: Int get() = 5

@get:kotlin.clr.ClrName("otherProperty")
val Set<Int>.firstProperty: Int get() = 6

val Collection<Int>.otherProperty: Int get() = 7

fun checkLocalNominalOverloads() {
    check(NominalArrayConstructor(arrayOf(1, 2)).chosen == 1)
    check(NominalArrayConstructor(intArrayOf(1, 2)).chosen == 2)
    check(NominalNullableConstructor(ints = listOf(1, null)).chosen == 1)
    check(NominalNullableConstructor(longs = listOf(1L, null)).chosen == 2)
    val collection: Collection<Int> = listOf(1)
    val set: Set<Int> = setOf(1)
    check(collection.left() == 1 && set.right() == 2)
    check(collection.ordinaryPhysicalName() == 3 && set.ordinaryPhysicalName() == 4)
    check(collection.firstProperty == 5 && set.firstProperty == 6 && collection.otherProperty == 7)
}
