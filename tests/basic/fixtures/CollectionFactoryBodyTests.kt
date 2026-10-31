import NUnit.Framework.TestAttribute

private fun <T> bodyList(vararg values: T): List<T> = listOf(*values)
private fun <T> bodyMutableList(vararg values: T): MutableList<T> = mutableListOf(*values)
private fun <T> bodyArrayList(vararg values: T): ArrayList<T> = arrayListOf(*values)
private fun <T> bodyHashSet(vararg values: T): HashSet<T> = hashSetOf(*values)
private fun bodyIndices(values: Collection<*>): IntRange = values.indices
private fun nullableBodyValue(value: Int): Int? = if (value < 0) null else value
private fun mutateBodyPack(vararg values: Int) { values[0] = 99 }
private inline fun mutateInlineBodyPack(vararg values: Int) { values[0] = 88 }

private class FactoryHashValue(val value: Int, private val observe: (String) -> Unit) {
    override fun hashCode(): Int {
        observe("h")
        return value
    }
    override fun equals(other: Any?): Boolean {
        observe("e")
        return other is FactoryHashValue && other.value == value
    }
}

class CollectionFactoryBodyTests {
    @TestAttribute fun emptyFactoriesKeepNothingAndNullableContracts() {
        val nothingList = emptyList<Nothing>()
        val nothingSet = emptySet<Nothing>()
        val lists: List<String?> = nothingList
        val sets: Set<Int?> = nothingSet
        check(lists.isEmpty() && sets.isEmpty())
        check(lists == bodyList<String?>())
        check(sets == bodyHashSet<Int?>())
        check(lists.hashCode() == 1 && sets.hashCode() == 0)
        check(!nothingList.iterator().hasNext())
        check(!nothingSet.iterator().hasNext())
        val mutable = bodyMutableList<Int?>()
        mutable.add(null)
        mutable.add(42)
        check(mutable.size == 2 && mutable[0] == null && mutable[1] == 42)
        val arrayList = bodyArrayList<String>()
        arrayList.add("first")
        check(arrayList.single() == "first")
    }

    @TestAttribute fun forwardedAndMixedPacksPreserveElementsAndIndependentStorage() {
        val values = arrayOf<Int?>(1, null, 3)
        val list = bodyList(*values)
        val mutable = bodyMutableList(0, *values, 4)
        val arrayList = bodyArrayList(*values)
        values[0] = 99
        check(list.size == 3 && list[0] == 1 && list[1] == null) { "Spread list must retain its original values" }
        check(mutable.size == 5 && mutable[1] == 1 && mutable[2] == null && mutable[4] == 4) {
            "Mixed nullable pack must preserve order, null and direct values"
        }
        arrayList[0] = 7
        arrayList.add(8)
        check(arrayList.size == 4 && arrayList[0] == 7 && list[0] == 1)
        val singleArray = listOf(values)
        check(singleArray.size == 1 && singleArray[0] === values)
        check(bodyList(values, values).all { it === values })
        check(listOf(System.TimeSpan.FromTicks(7)).single().Ticks == 7L)
        check(bodyList(System.TimeSpan.FromTicks(9)).single().Ticks == 9L)
        val nativeValues = arrayOf(System.TimeSpan.FromTicks(3), System.TimeSpan.FromTicks(4))
        val nativeList = bodyList(*nativeValues)
        val nativeArrayList = bodyArrayList(*nativeValues)
        nativeValues[0] = System.TimeSpan.FromTicks(99)
        check(nativeList[0].Ticks == 3L && nativeList[1].Ticks == 4L)
        check(nativeArrayList[0].Ticks == 3L && nativeArrayList[1].Ticks == 4L)
        val nullableNative = bodyArrayList<System.TimeSpan?>(null, System.TimeSpan.FromTicks(5))
        check(nullableNative[0] == null && nullableNative[1]!!.Ticks == 5L)
        val nested = bodyArrayList(values, values)
        check(nested[0] === values && nested[1] === values)
        val widened = bodyList<Any>(0, *arrayOf(1, 2), "last")
        check(widened == listOf<Any>(0, 1, 2, "last"))
        val primitive = intArrayOf(1, 2)
        mutateBodyPack(*primitive)
        check(primitive[0] == 1)
        mutateInlineBodyPack(*primitive)
        check(primitive[0] == 1)
        check(varargSpreadConcatInts(*uintArrayOf(UInt.MAX_VALUE).asIntArray()) == "-1")
        val nullable = nullableBodyValue(7)
        val absent = nullableBodyValue(-1)
        check(bodyMutableList(nullable, *values)[0] == 7)
        check(bodyMutableList(absent, *values)[0] == null)
    }

    @TestAttribute fun starProjectedIndicesKeepValueAndReferenceReceivers() {
        check(bodyIndices(bodyList(4, 5, 6)).toList() == listOf(0, 1, 2))
        check(bodyIndices(bodyMutableList("a", "b")).toList() == listOf(0, 1))
        check(bodyIndices(bodyArrayList<Int?>(null, 2)).last == 1)
        check(bodyIndices(emptySet<Nothing>()).isEmpty())
        check(bodyIndices(bodyHashSet(4, 4, 5)).last == 1)
    }

    @TestAttribute fun hashFactoriesKeepKotlinFloatingAndNullableEquality() {
        val otherNaN = System.BitConverter.Int64BitsToDouble(0x7ff8000000000001L)
        val values = bodyHashSet(0.0, -0.0, Double.NaN, otherNaN)
        check(values.size == 3)
        check(values.contains(0.0) && values.contains(-0.0) && values.contains(Double.NaN))
        check(values.remove(-0.0))
        check(values.contains(0.0) && !values.contains(-0.0))
        val nullable = bodyHashSet<Int?>(null, 1, null, 1, 2)
        check(nullable.size == 3 && nullable.contains(null) && nullable.contains(2))
        check(nullable == setOf<Int?>(2, null, 1))
        check(nullable.hashCode() == setOf<Int?>(2, null, 1).hashCode())
    }

    @TestAttribute fun allArgumentsAreEvaluatedBeforeHashCallbacks() {
        var trace = ""
        fun value(label: String, key: Int): FactoryHashValue {
            trace += label
            return FactoryHashValue(key) { trace += it }
        }
        val values = hashSetOf(value("a", 1), *arrayOf(value("b", 2)), value("c", 1))
        check(trace.startsWith("abc"))
        check(trace.contains("h") && trace.contains("e"))
        check(values.size == 2)
        check(values.contains(FactoryHashValue(1) {}))
        trace = ""
        fun failSource(): Array<FactoryHashValue> {
            trace += "x"
            throw IllegalStateException("expected spread failure")
        }
        var threw = false
        try {
            hashSetOf(value("d", 3), *failSource(), value("z", 4))
        } catch (failure: IllegalStateException) {
            check(failure.message == "expected spread failure")
            threw = true
        }
        check(threw && trace == "dx")
    }
}
