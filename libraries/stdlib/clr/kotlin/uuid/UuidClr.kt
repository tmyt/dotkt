@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")
@file:OptIn(ExperimentalUnsignedTypes::class)

package kotlin.uuid

@kotlin.clr.ClrTypeAlias("System.Security.Cryptography.RandomNumberGenerator")
private abstract class ClrUuidRandom {
    @kotlin.clr.ClrIntrinsic("GetBytes")
    fun fill(destination: UByteArray): Unit = TODO("clr binding should be implemented")

    @kotlin.clr.ClrIntrinsic("Dispose")
    fun dispose(): Unit = TODO("clr binding should be implemented")
}

@kotlin.clr.ClrIntrinsic("System.Security.Cryptography.RandomNumberGenerator.Create")
private fun createUuidRandom(): ClrUuidRandom = TODO("clr binding should be implemented")

@ExperimentalUuidApi
internal actual fun secureRandomBytes(destination: ByteArray) {
    val random = createUuidRandom()
    try {
        // The unsigned view shares the destination storage; the native fill needs no copyback.
        random.fill(destination.asUByteArray())
    } finally {
        random.dispose()
    }
}

@ExperimentalUuidApi
internal actual fun serializedUuid(uuid: Uuid): Any =
    throw UnsupportedOperationException("Serialization is supported only on the JVM")

// The byte<->Long packing and hex parsing are pure, big-endian, platform-agnostic
// algorithms shared in common as `*CommonImpl`. We delegate to them (as the JVM
// actual does). Note: we intentionally do NOT route through System.Guid, whose
// byte order for the first three fields differs from Kotlin's Uuid layout.

@ExperimentalUuidApi
internal actual fun ByteArray.getLongAt(index: Int): Long = getLongAtCommonImpl(index)

@ExperimentalUuidApi
internal actual fun Long.formatBytesInto(dst: ByteArray, dstOffset: Int, startIndex: Int, endIndex: Int): Unit =
    formatBytesIntoCommonImpl(dst, dstOffset, startIndex, endIndex)

@ExperimentalUuidApi
internal actual fun ByteArray.setLongAt(index: Int, value: Long): Unit =
    setLongAtCommonImpl(index, value)

@ExperimentalUuidApi
internal actual fun uuidParseHexDash(hexDashString: String): Uuid =
    uuidParseHexDashCommonImpl(hexDashString)

@ExperimentalUuidApi
internal actual fun uuidParseHexDashOrNull(hexDashString: String): Uuid? =
    uuidParseHexDashOrNullCommonImpl(hexDashString)

@ExperimentalUuidApi
internal actual fun uuidParseHex(hexString: String): Uuid =
    uuidParseHexCommonImpl(hexString)

@ExperimentalUuidApi
internal actual fun uuidParseHexOrNull(hexString: String): Uuid? =
    uuidParseHexOrNullCommonImpl(hexString)
