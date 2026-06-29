@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM actual; bodies are TODO pending the @Clr/BCL binding step
// (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.io.encoding

// CLR has no scheme-aware Base64 in the BCL (System.Convert always uses the basic
// RFC 4648 alphabet with mandatory padding and cannot honor a Base64 instance's
// UrlSafe/Mime/padding configuration), so these platform hooks delegate to the
// shared, scheme-aware `*Impl` members on the Base64 receiver. This mirrors the
// JVM slow path and the JS/Native actuals.

@SinceKotlin("1.8")
@kotlin.internal.InlineOnly
internal actual inline fun Base64.platformCharsToBytes(source: CharSequence, startIndex: Int, endIndex: Int): ByteArray =
    charsToBytesImpl(source, startIndex, endIndex)

@SinceKotlin("1.8")
@kotlin.internal.InlineOnly
internal actual inline fun Base64.platformEncodeToString(source: ByteArray, startIndex: Int, endIndex: Int): String =
    bytesToStringImpl(encodeToByteArrayImpl(source, startIndex, endIndex))

@SinceKotlin("1.8")
@kotlin.internal.InlineOnly
internal actual inline fun Base64.platformEncodeIntoByteArray(
    source: ByteArray,
    destination: ByteArray,
    destinationOffset: Int,
    startIndex: Int,
    endIndex: Int
): Int = encodeIntoByteArrayImpl(source, destination, destinationOffset, startIndex, endIndex)

@SinceKotlin("1.8")
@kotlin.internal.InlineOnly
internal actual inline fun Base64.platformEncodeToByteArray(
    source: ByteArray,
    startIndex: Int,
    endIndex: Int
): ByteArray = encodeToByteArrayImpl(source, startIndex, endIndex)
