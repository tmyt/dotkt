@file:OptIn(ExperimentalUnsignedTypes::class)

import NUnit.Framework.TestAttribute

class UnsignedArrayMemberTests {
    @TestAttribute
    fun queriesKeepUnsignedDomainsAcrossAllFourWidths() {
        val bytes = ubyteArrayOf(0u, 128u, UByte.MAX_VALUE)
        val shorts = ushortArrayOf(0u, 32768u, UShort.MAX_VALUE)
        val ints = uintArrayOf(0u, 2147483648u, UInt.MAX_VALUE)
        val longs = ulongArrayOf(0uL, 9223372036854775808uL, ULong.MAX_VALUE)
        check(UByte.MAX_VALUE in bytes && UShort.MAX_VALUE in shorts)
        check(UInt.MAX_VALUE in ints && ULong.MAX_VALUE in longs)
        check(bytes.containsAll(listOf<UByte>(0u, 128u, UByte.MAX_VALUE, UByte.MAX_VALUE)))
        check(shorts.containsAll(listOf<UShort>(0u, 32768u, UShort.MAX_VALUE)))
        check(ints.containsAll(listOf(0u, 2147483648u, UInt.MAX_VALUE)))
        check(longs.containsAll(listOf(0uL, 9223372036854775808uL, ULong.MAX_VALUE)))
        check(!bytes.containsAll(listOf<UByte>(7u)) && !shorts.containsAll(listOf<UShort>(7u)))
        check(!ints.containsAll(listOf(7u)) && !longs.containsAll(listOf(7uL)))
        check(bytes.containsAll(emptyList()) && shorts.containsAll(emptyList()))
        check(ints.containsAll(emptyList()) && longs.containsAll(emptyList()))
        check(!bytes.isEmpty() && !shorts.isEmpty() && !ints.isEmpty() && !longs.isEmpty())
        check(ubyteArrayOf().isEmpty() && ushortArrayOf().isEmpty())
        check(uintArrayOf().isEmpty() && ulongArrayOf().isEmpty())
        check(bytes.isNotEmpty() && ints.isNotEmpty())
    }

    @TestAttribute
    fun guardedBulkQueriesAndMemberReferencesKeepLiveStorage() {
        check(!ubyteArrayOf(UByte.MAX_VALUE).containsAll(listOf<Any?>(UByte.MAX_VALUE, null) as Collection<UByte>))
        check(!ushortArrayOf(UShort.MAX_VALUE).containsAll(listOf<Any?>(UShort.MAX_VALUE, "other") as Collection<UShort>))
        check(!uintArrayOf(UInt.MAX_VALUE).containsAll(listOf<Any?>(UInt.MAX_VALUE, -1) as Collection<UInt>))
        check(!ulongArrayOf(ULong.MAX_VALUE).containsAll(listOf<Any?>(ULong.MAX_VALUE, -1L) as Collection<ULong>))
        check(!ubyteArrayOf(UByte.MAX_VALUE).containsAll(listOf<Any?>((-1).toByte()) as Collection<UByte>))
        check(!ushortArrayOf(UShort.MAX_VALUE).containsAll(listOf<Any?>((-1).toShort()) as Collection<UShort>))
        val signed = intArrayOf(-1, 1)
        val values = signed.asUIntArray()
        val query = values::containsAll
        check(query(listOf(UInt.MAX_VALUE)) && !values.isEmpty())
        signed[0] = 7
        check(query(listOf(7u)) && !query(listOf(UInt.MAX_VALUE)))
        values[1] = 0u
        check(signed[1] == 0)
        val empty = uintArrayOf()
        val emptyQuery = empty::containsAll
        check(emptyQuery(emptyList()) && !emptyQuery(listOf(0u)))
    }
}
