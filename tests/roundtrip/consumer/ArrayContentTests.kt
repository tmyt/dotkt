package roundtriptests.arraycontent

import NUnit.Framework.TestAttribute
import roundtrip.arraycontent.*

private fun <T> checkImportedDeepContents(first: Array<out T>?, second: Array<out T>?, rendering: String) {
    check(deepEqualsInLibrary(first, second))
    var callbacks = 0
    var evaluations = 0
    fun firstOnce(): Array<out T>? { evaluations++; return first }
    check(inlineDeepEquals(firstOnce(), second) { callbacks++; it })
    check(boundDeepEquals(first)(second))
    check(deepHashInLibrary(first) == first.contentDeepHashCode())
    check(inlineDeepHash(firstOnce()) { callbacks++; it } == second.contentDeepHashCode())
    check(boundDeepHash(first)() == second.contentDeepHashCode())
    check(deepStringInLibrary(first) == rendering)
    check(inlineDeepString(firstOnce()) { callbacks++; it } == rendering)
    check(boundDeepString(first)() == rendering)
    check(callbacks == 3 && evaluations == 3)
}

class ArrayContentTests {
    @TestAttribute
    fun importedGenericArrayOperationsDoNotForceTheCallersElementType() {
        check(importedErasedArrayContent<ImportedArrayItem>("equals") == true)
        check(importedErasedArrayContent<ImportedArrayItem>("hash") == 38)
        check(importedErasedArrayContent<ImportedArrayItem>("string") == "[node]")
        check(importedNullArrayContent<Int>("equals") == true)
        check(importedNullArrayContent<Int>("hash") == 31)
        check(importedNullArrayContent<Int>("string") == "[null]")
        check(importedNullArrayContent<String>("equals") == true)
        check(importedNullArrayContent<String>("hash") == 31)
        check(importedNullArrayContent<String>("string") == "[null]")
        check(importedArrayVirtualLabel(arrayOf(ImportedArrayItem())) == "item")
    }

    @TestAttribute
    fun importedInlineBodiesAndCallableReferencesUseSharedDeepOperations() {
        val first = makeArrayContents()
        val second = makeArrayContents()
        checkImportedDeepContents(first, second, "[[value:7, null], tail]")
        check(first.contentDeepHashCode() == 31 * (31 + 31 * (31 + 91)) + "tail".hashCode())
        check(!deepEqualsInLibrary(first, arrayOf<Any?>(arrayOf(ArrayContentValue(8), null), "tail")))
        checkImportedDeepContents(arrayOf(11, 13), arrayOf(11, 13), "[11, 13]")
        checkImportedDeepContents(arrayOf<Int?>(11, null), arrayOf<Int?>(11, null), "[11, null]")
        checkImportedDeepContents(emptyArray<String>(), emptyArray<String>(), "[]")
        checkImportedDeepContents(primitiveArrayContents(), primitiveArrayContents(),
            "[[-1, 2], [-3, 4], [-5, 6], [-7, 8], [1.5, 2.5], [3.5, 4.5], " +
            "[True, False], [x, y], [255, 2], [65535, 4], [4294967295, 6], [18446744073709551615, 8]]")
        checkImportedDeepContents<Any?>(null, null, "null")
        check(deepHashInLibrary<Any?>(null) == 0)
        check(!inlineDeepEquals<Any?>(null, emptyArray<Any?>()) { it })
    }

    @TestAttribute
    fun importedRenderingPreservesCycleAndRepeatedChildSemantics() {
        val self = arrayOfNulls<Any>(1)
        self[0] = self
        check(deepStringInLibrary(self) == "[[...]]")
        check(inlineDeepString(self) { it } == "[[...]]")
        check(boundDeepString(self)() == "[[...]]")
        val left = arrayOfNulls<Any>(1)
        val right = arrayOfNulls<Any>(1)
        left[0] = right
        right[0] = left
        check(deepStringInLibrary(left) == "[[[...]]]")
        val child = arrayOf("shared")
        checkImportedDeepContents(arrayOf(child, child), arrayOf(arrayOf("shared"), arrayOf("shared")),
            "[[shared], [shared]]")
    }

    @TestAttribute
    fun importedFloatingEqualityRetainsStructuralNanAndSignedZeroRules() {
        val firstFloat = floatArrayOf(System.BitConverter.Int32BitsToSingle(0x7fc00001))
        val secondFloat = floatArrayOf(System.BitConverter.Int32BitsToSingle(0x7fc00002))
        val firstDouble = doubleArrayOf(System.BitConverter.Int64BitsToDouble(0x7ff8000000000001L))
        val secondDouble = doubleArrayOf(System.BitConverter.Int64BitsToDouble(0x7ff8000000000002L))
        check(floatContentsInLibrary(firstFloat, secondFloat))
        check(inlineFloatContents(firstFloat, secondFloat) { it })
        check(boundFloatContents(firstFloat)(secondFloat))
        check(doubleContentsInLibrary(firstDouble, secondDouble))
        check(inlineDoubleContents(firstDouble, secondDouble) { it })
        check(boundDoubleContents(firstDouble)(secondDouble))
        check(!floatContentsInLibrary(floatArrayOf(-0.0f), floatArrayOf(0.0f)))
        check(!inlineDoubleContents(doubleArrayOf(-0.0), doubleArrayOf(0.0)) { it })
        check(floatContentsInLibrary(null, null) && doubleContentsInLibrary(null, null))
        check(!boundFloatContents(null)(floatArrayOf()))
        check(!boundDoubleContents(doubleArrayOf())(null))
        checkImportedDeepContents(arrayOf<Any?>(firstFloat, firstDouble), arrayOf<Any?>(secondFloat, secondDouble),
            "[[NaN], [NaN]]")
    }

    @TestAttribute
    fun importedArrayProjectionCollectionsRetainMembershipAndIteration() {
        val first = arrayOf(1, 2)
        val second = arrayOf("second")
        val values = importedArrayList(first)
        check(importedArrayContains(values, first) && !importedArrayContains(values, second))
        values.add(second)
        check(importedArrayContains(values, second) && second in values)
        check(importedArraySizes(values) == 3)
        check(arrayOf(1, 2) !in values)
    }
}
