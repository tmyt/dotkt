@file:OptIn(ExperimentalUnsignedTypes::class)

package roundtriptests.unsignedarraymembers

import NUnit.Framework.TestAttribute
import roundtrip.unsignedarraymembers.*

class UnsignedArrayMemberTests {
    @TestAttribute
    fun importedBodiesAndSavedMemberReferencesKeepUnsignedFrames() {
        check(queryBytes(ubyteArrayOf(UByte.MAX_VALUE), listOf(UByte.MAX_VALUE)))
        check(queryShorts(ushortArrayOf(UShort.MAX_VALUE), listOf(UShort.MAX_VALUE)))
        check(queryInts(uintArrayOf(UInt.MAX_VALUE), listOf(UInt.MAX_VALUE)))
        check(queryLongs(ulongArrayOf(ULong.MAX_VALUE), listOf(ULong.MAX_VALUE)))
        check(!queryInts(uintArrayOf(UInt.MAX_VALUE), listOf<Any?>(UInt.MAX_VALUE, null) as Collection<UInt>))
        check(inlineLongQuery(ulongArrayOf(ULong.MAX_VALUE), listOf(ULong.MAX_VALUE)))
        val values = uintArrayOf(UInt.MAX_VALUE)
        val query = savedIntQuery(values)
        check(query(listOf(UInt.MAX_VALUE)))
        values[0] = 7u
        check(query(listOf(7u)) && !query(listOf(UInt.MAX_VALUE)))
        check(savedIntQuery(uintArrayOf())(emptyList()))
    }
}
