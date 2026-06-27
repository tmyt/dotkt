@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM actual; bodies are TODO pending the @Clr/BCL binding step
// (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.io.encoding

@SinceKotlin("1.8")
@kotlin.internal.InlineOnly
internal actual inline fun Base64.platformCharsToBytes(source: CharSequence, startIndex: Int, endIndex: Int): ByteArray =
    TODO("clr binding should be implemented")

@SinceKotlin("1.8")
@kotlin.internal.InlineOnly
internal actual inline fun Base64.platformEncodeToString(source: ByteArray, startIndex: Int, endIndex: Int): String =
    TODO("clr binding should be implemented")

@SinceKotlin("1.8")
@kotlin.internal.InlineOnly
internal actual inline fun Base64.platformEncodeIntoByteArray(
    source: ByteArray,
    destination: ByteArray,
    destinationOffset: Int,
    startIndex: Int,
    endIndex: Int
): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.8")
@kotlin.internal.InlineOnly
internal actual inline fun Base64.platformEncodeToByteArray(
    source: ByteArray,
    startIndex: Int,
    endIndex: Int
): ByteArray = TODO("clr binding should be implemented")
