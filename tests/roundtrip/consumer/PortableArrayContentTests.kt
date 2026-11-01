@file:OptIn(ExperimentalUnsignedTypes::class)

package roundtriptests.portablearraycontent

import NUnit.Framework.TestAttribute
import roundtrip.portablearraycontent.*

private class ImportedArrayValue {
    override fun hashCode(): Int = 37
    override fun toString(): String = "consumer"
}

class PortableArrayContentTests {
    @TestAttribute
    fun genericArrayContentAndInlineCallersSurviveSeparateDllCompilation() {
        check(erasedNullHash<Int>() == 31 && erasedNullText<Int>() == "[null]")
        check(arrayHash<String>(null) == 0 && arrayText<String>(null) == "null")
        check(arrayHash(emptyArray<String>()) == 1 && arrayText(emptyArray<String>()) == "[]")
        check(arrayEquals(arrayOf<Int?>(null, 7), arrayOf<Int?>(null, 7)))
        check(!arrayEquals(arrayOf(1), arrayOf(2)))
        check(arrayElement(arrayOf<Int?>(null, 7), 0) == null)
        check(arrayElement(arrayOf("first", "second"), 1) == "second")
        val value = ImportedArrayValue()
        check(arrayHash(arrayOf(value, null, value)) == 65385)
        check(arrayText(arrayOf(value, null)) == "[consumer, null]")
        check(inlineArrayText(arrayOf<Int?>(null, 7)) == "[null, 7]")
        check(unsignedElement(uintArrayOf(UInt.MAX_VALUE), 0) == UInt.MAX_VALUE)
    }

    @TestAttribute
    fun importedCallableReferencesReadLiveArrayStorage() {
        val ints = intArrayOf(1, 2)
        val hash = savedHash(ints)
        check(hash() == 994)
        ints[0] = 7
        check(hash() == 1180)
        val text = arrayOf<String?>("first", null)
        val render = savedText(text)
        check(render() == "[first, null]")
        text[0] = "changed"
        text[1] = "last"
        check(render() == "[changed, last]")
    }
}
