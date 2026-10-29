import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private fun <T : Comparable<T>> stableGenericCompare(left: T, right: T): Int = left.compareTo(right)
private fun stableDoubleCompare(left: Double, right: Double): Int = left.compareTo(right)
private fun stableFloatCompare(left: Float, right: Float): Int = left.compareTo(right)

@Suppress("UNCHECKED_CAST")
private fun <T : Comparable<T>> assertFloatingComparisons(values: Array<T>) {
    for (i in values.indices) for (j in values.indices) {
        val expected = if (i >= 6 && j >= 6) 0 else i.compareTo(j)
        val left = values[i]
        val right = values[j]
        assertEquals(expected, stableGenericCompare(left, right).compareTo(0))
        val typed: Comparable<T> = left
        assertEquals(expected, typed.compareTo(right).compareTo(0))
        val erased = left as Comparable<Any>
        assertEquals(expected, erased.compareTo(right).compareTo(0))
        assertEquals(expected, compareValues(left, right).compareTo(0))
        assertEquals(expected, naturalOrder<T>().compare(left, right).compareTo(0))
    }
    check(compareValues<T>(null, values[0]) < 0 && compareValues<T>(values[0], null) > 0)
}

private class StableSortTag<T>(val key: Int, val tag: T) : Comparable<StableSortTag<T>> {
    override fun compareTo(other: StableSortTag<T>): Int = key.compareTo(other.key)
}

private class StableRecordingList<T>(private val values: MutableList<T>) : AbstractMutableList<T>() {
    var iterators = 0
    var reads = 0
    var writes = 0
    override val size: Int get() = values.size
    override fun get(index: Int): T = values[index]
    override fun set(index: Int, element: T): T { writes++; return values.set(index, element) }
    override fun add(index: Int, element: T) { values.add(index, element) }
    override fun removeAt(index: Int): T = values.removeAt(index)
    override fun listIterator(): MutableListIterator<T> {
        iterators++
        val underlying = super.listIterator()
        return object : MutableListIterator<T> by underlying {
            override fun next(): T { reads++; return underlying.next() }
        }
    }
}

class StableArraySortTests {
    @TestAttribute fun floatingComparisonsShareKotlinTotalOrderAcrossViews() {
        val doubles = arrayOf(Double.NEGATIVE_INFINITY, -1.0, -0.0, 0.0, 1.0, Double.POSITIVE_INFINITY,
            Double.NaN, System.BitConverter.Int64BitsToDouble(0x7ff8000000000001L))
        val floats = arrayOf(Float.NEGATIVE_INFINITY, -1f, -0f, 0f, 1f, Float.POSITIVE_INFINITY,
            Float.NaN, System.BitConverter.Int32BitsToSingle(0x7fc00001))
        assertFloatingComparisons(doubles)
        assertFloatingComparisons(floats)
        for (i in doubles.indices) for (j in doubles.indices) {
            val expected = if (i >= 6 && j >= 6) 0 else i.compareTo(j)
            assertEquals(expected, stableDoubleCompare(doubles[i], doubles[j]).compareTo(0))
            assertEquals(expected, stableFloatCompare(floats[i], floats[j]).compareTo(0))
        }
    }

    @TestAttribute fun explicitNativeFloatingComparisonRetainsClrPolicy() {
        val rawDouble = (Double.NaN as Any) as System.IComparable
        val typedDouble = (Double.NaN as Any) as System.IComparable1<Double>
        check(rawDouble.CompareTo(1.0) < 0 && typedDouble.CompareTo(1.0) < 0)
        check((( -0.0 as Any) as System.IComparable).CompareTo(0.0) == 0)
        check((( -0.0 as Any) as System.IComparable1<Double>).CompareTo(0.0) == 0)
        val rawFloat = (Float.NaN as Any) as System.IComparable
        val typedFloat = (Float.NaN as Any) as System.IComparable1<Float>
        check(rawFloat.CompareTo(1f) < 0 && typedFloat.CompareTo(1f) < 0)
        check((( -0f as Any) as System.IComparable).CompareTo(0f) == 0)
        check((( -0f as Any) as System.IComparable1<Float>).CompareTo(0f) == 0)
        check(rawDouble.CompareTo(null) > 0 && rawFloat.CompareTo(null) > 0)
        var rejected = false
        try { rawDouble.CompareTo("wrong type") } catch (_: IllegalArgumentException) { rejected = true }
        check(rejected)
    }

    @TestAttribute fun bothArraySortsAreStableAndKeepNativeStorage() {
        for (size in 0..33) {
            val original = Array(size) { StableSortTag((it * 17) % 5, it) }
            for (explicit in 0..1) {
                val values = original.copyOf()
                val native = values as System.Array
                if (explicit == 0) values.sort()
                else values.sortWith(Comparator { left, right -> left.key.compareTo(right.key) })
                check(values === native && native.Length == size)
                for (i in 1 until size) {
                    check(values[i - 1].key <= values[i].key)
                    if (values[i - 1].key == values[i].key) check(values[i - 1].tag < values[i].tag)
                }
                for (value in original) check(values.count { it === value } == 1)
                for (i in values.indices) check(native.GetValue(i) === values[i])
            }
        }
        val nullable = arrayOf<Int?>(2, null, 1, null)
        nullable.sortWith(Comparator { left, right -> compareValues(left, right) })
        check(nullable.contentEquals(arrayOf<Int?>(null, null, 1, 2)))
        val strings = arrayOf("c", "a", "b"); strings.sort()
        check(strings.contentEquals(arrayOf("a", "b", "c")))
    }

    @TestAttribute fun arrayRangesValidateBeforeComparingOrWriting() {
        for (explicit in 0..1) {
            for (from in 0..6) for (to in from..6) {
                val values = Array(6) { StableSortTag((it * 7) % 3, it) }
                val original = values.copyOf()
                if (explicit == 0) values.sort(from, to)
                else values.sortWith(Comparator { a, b -> a.key.compareTo(b.key) }, from, to)
                for (i in values.indices) if (i < from || i >= to) check(values[i] === original[i])
                for (i in from + 1 until to) {
                    check(values[i - 1].key <= values[i].key)
                    if (values[i - 1].key == values[i].key) check(values[i - 1].tag < values[i].tag)
                }
            }
            val froms = intArrayOf(-1, 0, 2, 4, -1, Int.MIN_VALUE, 0)
            val tos = intArrayOf(2, 4, 1, 4, -2, Int.MAX_VALUE, -1)
            for (i in froms.indices) {
                val values = arrayOf(3, 2, 1)
                var comparisons = 0
                val comparator = Comparator<Int> { a, b -> comparisons++; a.compareTo(b) }
                var failure = "none"
                try {
                    if (explicit == 0) values.sort(froms[i], tos[i])
                    else values.sortWith(comparator, froms[i], tos[i])
                } catch (_: IndexOutOfBoundsException) { failure = "bounds" }
                catch (_: IllegalArgumentException) { failure = "inverted" }
                assertEquals(if (i == 2 || i == 6) "inverted" else "bounds", failure)
                check(comparisons == 0 && values.contentEquals(arrayOf(3, 2, 1)))
            }
        }
    }

    @TestAttribute fun mutableListsUseIteratorWritebackAndPreserveFailureInputs() {
        val values = StableRecordingList(mutableListOf(3, 1, 2))
        values.sort()
        check(values == listOf(1, 2, 3))
        check(values.iterators >= 1 && values.reads >= 3 && values.writes == 3)
        val parent = mutableListOf(99, 4, 1, 3, 2, 88)
        parent.subList(1, 5).sortWith(Comparator { a, b -> a.compareTo(b) })
        check(parent == listOf(99, 1, 2, 3, 4, 88))
        val input = StableRecordingList(mutableListOf(5, 4, 3, 2, 1))
        val failure = IllegalStateException("sort comparison failure")
        var comparisons = 0
        var caught = false
        try { input.sortWith(Comparator { a, b ->
            comparisons++
            if (comparisons == 3) throw failure
            a.compareTo(b)
        }) } catch (actual: IllegalStateException) { check(actual === failure); caught = true }
        check(caught && input.writes == 0 && input == listOf(5, 4, 3, 2, 1))
    }

    @TestAttribute fun comparatorFailuresPropagateAndSmallInputsAvoidComparisons() {
        var comparisons = 0
        val failure = IllegalStateException("array comparison failure")
        val comparator = Comparator<Int> { _, _ -> comparisons++; throw failure }
        emptyArray<Int>().sortWith(comparator)
        arrayOf(7).sortWith(comparator)
        mutableListOf<Int>().sortWith(comparator)
        mutableListOf(7).sortWith(comparator)
        check(comparisons == 0)
        val values = arrayOf(99, 3, 2, 1, 88)
        var caught = false
        try { values.sortWith(comparator, 1, 4) }
        catch (actual: IllegalStateException) { check(actual === failure); caught = true }
        check(caught && comparisons == 1 && values.contentEquals(arrayOf(99, 3, 2, 1, 88)))
    }

    @TestAttribute fun floatingSortsKeepSignedZerosAndStableNanPayloads() {
        val nan1 = System.BitConverter.Int64BitsToDouble(0x7ff8000000000001L)
        val nan2 = System.BitConverter.Int64BitsToDouble(Long.MIN_VALUE or 0x7ff8000000000002L)
        val input = arrayOf(nan1, 0.0, -0.0, Double.NEGATIVE_INFINITY, 2.0, nan2, Double.POSITIVE_INFINITY, -2.0)
        val expected = arrayOf(Double.NEGATIVE_INFINITY, -2.0, -0.0, 0.0, 2.0, Double.POSITIVE_INFINITY, nan1, nan2)
        for (mode in 0..3) {
            val values = input.copyOf()
            when (mode) {
                0 -> values.sort()
                1 -> values.sort(0, values.size)
                2 -> values.sortWith(naturalOrder())
                else -> {
                    val list = values.toMutableList()
                    list.sort()
                    for (i in values.indices) values[i] = list[i]
                }
            }
            for (i in values.indices)
                assertEquals(System.BitConverter.DoubleToInt64Bits(expected[i]), System.BitConverter.DoubleToInt64Bits(values[i]))
        }
        val floats = arrayOf(Float.NaN, 0f, -0f, Float.NEGATIVE_INFINITY, Float.POSITIVE_INFINITY)
        floats.sort()
        check(floats[0] == Float.NEGATIVE_INFINITY && floats[3] == Float.POSITIVE_INFINITY && floats[4].isNaN())
        check(System.BitConverter.SingleToInt32Bits(floats[1]) == Int.MIN_VALUE)
        check(System.BitConverter.SingleToInt32Bits(floats[2]) == 0)
    }
}
