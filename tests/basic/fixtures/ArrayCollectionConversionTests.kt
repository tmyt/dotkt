import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsInstanceOf as assertInstanceOf

private class ArrayConversionCollection<T>(private val values: List<T>) : AbstractCollection<T>() {
    var iteratorCalls = 0
    override val size: Int get() = values.size
    override fun iterator(): Iterator<T> { iteratorCalls++; return values.iterator() }
    fun objects(): Array<Any?> = toArray()
    fun <R> into(destination: Array<R>): Array<R> = toArray(destination)
}

class ArrayCollectionConversionTests {
    @TestAttribute
    fun inheritedUntypedConversionIncludesEveryElement() {
        val source = ArrayConversionCollection(listOf<Any?>(7, "text", null))
        val result = source.objects()
        check(result.size == 3 && result[0] == 7 && result[1] == "text" && result[2] == null)
        assertInstanceOf<Array<Any?>>(result)
        check(source.iteratorCalls == 1)
        val empty = ArrayConversionCollection(emptyList<Any?>())
        check(empty.objects().isEmpty())
        check(empty.iteratorCalls == 0)
    }

    @TestAttribute
    fun inheritedTypedConversionReallocatesWithTheDestinationComponent() {
        val integers = ArrayConversionCollection(listOf(7, 9, 11))
        val small = arrayOf(99)
        val result = integers.into(small)
        check(result !== small && result.size == 3)
        check(result[0] == 7 && result[1] == 9 && result[2] == 11 && small[0] == 99)
        assertInstanceOf<IntArray>(result)
        check(integers.iteratorCalls == 1)
        val strings = ArrayConversionCollection(listOf("first", "second"))
        val names = strings.into(emptyArray<String>())
        check(names.size == 2 && names[0] == "first" && names[1] == "second")
        assertInstanceOf<Array<String>>(names)
        val nullable = ArrayConversionCollection(listOf<Int?>(null, 7))
        val boxed = nullable.into(arrayOfNulls<Int>(1))
        check(boxed.size == 2 && boxed[0] == null && boxed[1] == 7)
        assertInstanceOf<Array<Any?>>(boxed)
        val duration = System.TimeSpan(0, 0, 5)
        val durations = ArrayConversionCollection(listOf(duration)).into(emptyArray<System.TimeSpan>())
        check(durations.size == 1 && durations[0].TotalSeconds == 5.0)
        assertInstanceOf<Array<System.TimeSpan>>(durations)
    }

    @TestAttribute
    fun inheritedTypedConversionReusesStorageAndLeavesTheSpareTailAlone() {
        val source = ArrayConversionCollection(listOf(7, 9))
        val exact = arrayOf(99, 98)
        check(source.into(exact) === exact && exact[0] == 7 && exact[1] == 9)
        val spare = arrayOf(99, 98, 97, 96)
        check(source.into(spare) === spare)
        check(spare[0] == 7 && spare[1] == 9 && spare[2] == 97 && spare[3] == 96)
        val names = arrayOf("old", "tail", "last")
        check(ArrayConversionCollection(listOf("new")).into(names) === names)
        check(names[0] == "new" && names[1] == "tail" && names[2] == "last")
        val empty = ArrayConversionCollection(emptyList<Int>())
        val unchanged = arrayOf(31, 41)
        check(empty.into(unchanged) === unchanged && unchanged[0] == 31 && unchanged[1] == 41)
        check(empty.iteratorCalls == 0)
    }

    @TestAttribute
    fun typedArraysUseOneIteratorWithoutBoxedStaging() {
        val integers = ArrayConversionCollection(listOf(7, 9))
        val result = integers.toTypedArray()
        check(result.size == 2 && result[0] == 7 && result[1] == 9)
        check(integers.iteratorCalls == 1)
        assertInstanceOf<IntArray>(result)
        val widened: Collection<Any?> = integers
        val objects = widened.toTypedArray()
        check(objects[0] == 7 && objects[1] == 9 && integers.iteratorCalls == 2)
        assertInstanceOf<Array<Any?>>(objects)
        val nullable = ArrayConversionCollection(listOf<Int?>(null, 7))
        val boxed = nullable.toTypedArray()
        check(boxed[0] == null && boxed[1] == 7 && nullable.iteratorCalls == 1)
        assertInstanceOf<Array<Any?>>(boxed)
        val strings = ArrayConversionCollection(listOf("first", "second"))
        val names = strings.toTypedArray()
        check(names[0] == "first" && names[1] == "second" && strings.iteratorCalls == 1)
        assertInstanceOf<Array<String>>(names)
        val duration = System.TimeSpan(0, 0, 5)
        val durations = ArrayConversionCollection(listOf(duration)).toTypedArray()
        check(durations[0].TotalSeconds == 5.0)
        assertInstanceOf<Array<System.TimeSpan>>(durations)
        val empty = ArrayConversionCollection(emptyList<Int>())
        check(empty.toTypedArray().isEmpty() && empty.iteratorCalls <= 1)
    }

    @TestAttribute
    fun orEmptyPreservesIdentityAndAllocatesTheReifiedComponent() {
        val integers = arrayOf(7)
        check(integers.orEmpty() === integers)
        val missing: Array<Int>? = null
        val result = missing.orEmpty()
        check(result.isEmpty())
        assertInstanceOf<IntArray>(result)
        val names: Array<String>? = null
        assertInstanceOf<Array<String>>(names.orEmpty())
        val nullable: Array<Int?>? = null
        assertInstanceOf<Array<Any?>>(nullable.orEmpty())
        val durations: Array<System.TimeSpan>? = null
        assertInstanceOf<Array<System.TimeSpan>>(durations.orEmpty())
    }
}
