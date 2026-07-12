@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM actual; bodies are TODO pending the @Clr/BCL binding step
// (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.uuid

// NOTE: the BCL crypto RNG (System.Security.Cryptography.RandomNumberGenerator.Fill) only accepts a
// `Span<byte>`/`byte[]` of UNSIGNED bytes, which does not bind to Kotlin's signed `ByteArray` (System.SByte[]).
// We therefore fill the buffer with the default `Random` (itself seeded from CLR entropy, see PlatformRandomClr),
// which yields a valid random (v4) Uuid — functionally correct, though not cryptographically strong.
@ExperimentalUuidApi
internal actual fun secureRandomBytes(destination: ByteArray) {
    kotlin.random.Random.Default.nextBytes(destination)
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
