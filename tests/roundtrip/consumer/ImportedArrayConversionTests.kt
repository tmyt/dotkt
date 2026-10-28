import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsInstanceOf as assertInstanceOf
import roundtrip.arrayconversion.LibraryArrayCollection
import roundtrip.arrayconversion.importedOrEmpty
import roundtrip.arrayconversion.importedTypedArray

class ImportedArrayConversionTests {
    @TestAttribute
    fun nestedInlineOrEmptyRetainsTheCallerElement() {
        val original = arrayOf(7, 9)
        check(importedOrEmpty(original) === original)
        val missing: Array<Int>? = null
        val integers = importedOrEmpty(missing)
        check(integers.isEmpty())
        assertInstanceOf<IntArray>(integers)
        val names: Array<String>? = null
        assertInstanceOf<Array<String>>(importedOrEmpty(names))
        val nullable: Array<Int?>? = null
        assertInstanceOf<Array<Any?>>(importedOrEmpty(nullable))
        val durations: Array<System.TimeSpan>? = null
        assertInstanceOf<Array<System.TimeSpan>>(importedOrEmpty(durations))
    }

    @TestAttribute
    fun importedCollectionAndInlineCopyRetainComponentsAndSingleIteration() {
        val integers = LibraryArrayCollection(listOf(7, 9))
        val copied = importedTypedArray(integers)
        check(copied.size == 2 && copied[0] == 7 && copied[1] == 9 && integers.iteratorCalls == 1)
        assertInstanceOf<IntArray>(copied)
        val grown = integers.into(emptyArray<Int>())
        check(grown.size == 2 && grown[0] == 7 && grown[1] == 9)
        assertInstanceOf<IntArray>(grown)
        val spare = arrayOf(99, 98, 97)
        check(integers.into(spare) === spare && spare[0] == 7 && spare[1] == 9 && spare[2] == 97)
        val objects = LibraryArrayCollection(listOf<Any?>(7, null)).objects()
        check(objects.size == 2 && objects[0] == 7 && objects[1] == null)
        val nullable = LibraryArrayCollection(listOf<Int?>(null, 7))
        val boxed = importedTypedArray(nullable)
        check(boxed[0] == null && boxed[1] == 7 && nullable.iteratorCalls == 1)
        assertInstanceOf<Array<Any?>>(boxed)
        val strings = LibraryArrayCollection(listOf("first", "second"))
        val names = importedTypedArray(strings)
        check(names[0] == "first" && names[1] == "second" && strings.iteratorCalls == 1)
        val duration = System.TimeSpan(0, 0, 5)
        val durations = importedTypedArray(LibraryArrayCollection(listOf(duration)))
        check(durations[0].TotalSeconds == 5.0)
        assertInstanceOf<Array<System.TimeSpan>>(durations)
    }
}
