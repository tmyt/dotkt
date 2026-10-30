@file:OptIn(kotlin.uuid.ExperimentalUuidApi::class, ExperimentalUnsignedTypes::class)

import NUnit.Framework.TestAttribute
import kotlin.time.Instant
import kotlin.uuid.Uuid

private fun checkUuidLayout(value: Uuid, version: Int) {
    val bytes = value.toByteArray()
    check(bytes.size == 16)
    check((bytes[6].toInt() and 0xf0) == (version shl 4))
    check((bytes[8].toInt() and 0xc0) == 0x80)
    check(Uuid.fromByteArray(bytes) == value)
    check(Uuid.fromUByteArray(bytes.asUByteArray()) == value)
    check(Uuid.parse(value.toHexDashString()) == value)
    check(Uuid.parseHex(value.toHexString()) == value)
}

class UuidEntropyTests {
    @TestAttribute fun randomAndVersionFourRetainCanonicalLayoutAndByteOrder() {
        val make: () -> Uuid = Uuid::generateV4
        repeat(16) {
            checkUuidLayout(Uuid.random(), 4)
            checkUuidLayout(Uuid.generateV4(), 4)
            checkUuidLayout(make(), 4)
        }
        val bytes = ByteArray(16) { (it * 0x11).toByte() }
        val known = Uuid.fromByteArray(bytes)
        check(known.toHexDashString() == "00112233-4455-6677-8899-aabbccddeeff")
        check(known.toByteArray().contentEquals(bytes))
    }

    @TestAttribute fun nonMonotonicVersionSevenRetainsTheSuppliedTimestamp() {
        val make: (Instant) -> Uuid = Uuid::generateV7NonMonotonicAt
        for (millis in longArrayOf(0L, 1L, 1_735_689_600_123L)) {
            val timestamp = Instant.fromEpochMilliseconds(millis)
            repeat(4) {
                val value = make(timestamp)
                checkUuidLayout(value, 7)
                check(value.toLongs { most, _ -> most ushr 16 } == millis)
            }
        }
    }

    @TestAttribute fun liveVersionSevenRetainsVersionVariantAndSequentialOrder() {
        var previous = Uuid.generateV7()
        checkUuidLayout(previous, 7)
        repeat(16) {
            val value = Uuid.generateV7()
            checkUuidLayout(value, 7)
            check(previous < value)
            previous = value
        }
    }

    @TestAttribute fun nativeRngConsumesSharedUnsignedViewsWithoutCopyback() {
        val random = System.Security.Cryptography.RandomNumberGenerator.Create()
        try {
            for (size in intArrayOf(0, 1, 10, 16, 31, 32)) {
                val bytes = ByteArray(size)
                val unsigned = bytes.asUByteArray()
                check(unsigned.asByteArray() === bytes)
                if (size > 0) {
                    unsigned[0] = 127u
                    check(bytes[0].toInt() == 127)
                }
                random.GetBytes(unsigned)
                for (index in bytes.indices) {
                    check((bytes[index].toInt() and 0xff) == unsigned[index].toInt())
                }
                check(unsigned.asByteArray() === bytes)
            }
        } finally {
            random.Dispose()
        }
    }
}
