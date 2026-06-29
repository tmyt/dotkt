@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM actual; bodies are TODO pending the @Clr/BCL binding step
// (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.uuid

@ExperimentalUuidApi
internal actual fun secureRandomUuid(): Uuid = TODO("clr binding should be implemented")

@ExperimentalUuidApi
internal actual fun serializedUuid(uuid: Uuid): Any = TODO("clr binding should be implemented")

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
internal actual fun uuidParseHex(hexString: String): Uuid =
    uuidParseHexCommonImpl(hexString)
