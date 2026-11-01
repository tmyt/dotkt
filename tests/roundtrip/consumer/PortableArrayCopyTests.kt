package roundtriptests.arraycopy

import NUnit.Framework.TestAttribute
import roundtrip.arraycopy.*

class PortableArrayCopyRoundtripTests {
    @TestAttribute fun genericCopiesCrossTheDllBoundary() {
        val values = arrayOf("first", "last")
        val copy = copyInLibrary(values)
        check(copy !== values && copy.contentEquals(values))
        check(resizeInLibrary(values, 3).contentEquals(arrayOf("first", "last", null)))
        check(rangeInLibrary(values, 1, 2).contentEquals(arrayOf("last")))
        check(appendInLibrary(values, arrayOf("tail")).contentEquals(arrayOf("first", "last", "tail")))
        check(resizeInLibrary(arrayOf(1, 2), 3).contentEquals(arrayOf<Int?>(1, 2, null)))
    }

    @TestAttribute fun primitiveAndBoxedCopiesCrossTheDllBoundary() {
        check(primitiveCopiesInLibrary(intArrayOf(1, 2, 3)).contentEquals(intArrayOf(2, 3, 7)))
        val values = intArrayOf(4, 5)
        val boxed = boxedCopyInLibrary(values)
        check(boxed.contentEquals(arrayOf(4, 5)))
        values[0] = 9
        check(boxed[0] == 4)
    }
}
