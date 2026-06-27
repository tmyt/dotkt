@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM actual; bodies are TODO pending the @Clr/BCL binding step
// (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.uuid

@ExperimentalUuidApi
internal actual fun secureRandomUuid(): Uuid = TODO("clr binding should be implemented")

@ExperimentalUuidApi
internal actual fun serializedUuid(uuid: Uuid): Any = TODO("clr binding should be implemented")

@ExperimentalUuidApi
internal actual fun ByteArray.getLongAt(index: Int): Long = TODO("clr binding should be implemented")

@ExperimentalUuidApi
internal actual fun Long.formatBytesInto(dst: ByteArray, dstOffset: Int, startIndex: Int, endIndex: Int): Unit =
    TODO("clr binding should be implemented")

@ExperimentalUuidApi
internal actual fun ByteArray.setLongAt(index: Int, value: Long): Unit =
    TODO("clr binding should be implemented")

@ExperimentalUuidApi
internal actual fun uuidParseHexDash(hexDashString: String): Uuid =
    TODO("clr binding should be implemented")

@ExperimentalUuidApi
internal actual fun uuidParseHex(hexString: String): Uuid =
    TODO("clr binding should be implemented")
