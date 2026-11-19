/*
 * Copyright 2010-2019 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

package kotlin.text

import kotlin.clr.byref
import kotlin.clr.Span
import System.Runtime.InteropServices.MemoryMarshal
import System.Text.Unicode.Utf8

// From Kotlin v2.4.21 libraries/stdlib/wasm/src/kotlin/text/utf8Encoding.kt (also used by JS).
// CLR bulk operations handle valid input; only the portable algorithm chooses malformed-input behavior.

/** Returns the negative [size] if [throwOnMalformed] is false, throws [CharacterCodingException] otherwise. */
private fun malformed(size: Int, index: Int, throwOnMalformed: Boolean): Int {
    if (throwOnMalformed) throw CharacterCodingException("Malformed sequence starting at ${index - 1}")
    return -size
}

/**
 * Returns code point corresponding to UTF-16 surrogate pair,
 * where the first of the pair is the [high] and the second is in the [string] at the [index].
 * Returns zero if the pair is malformed and [throwOnMalformed] is false.
 *
 * @throws CharacterCodingException if the pair is malformed and [throwOnMalformed] is true.
 */
private fun codePointFromSurrogate(string: String, high: Int, index: Int, endIndex: Int, throwOnMalformed: Boolean): Int {
    if (high !in 0xD800..0xDBFF || index >= endIndex) {
        return malformed(0, index, throwOnMalformed)
    }
    val low = string[index].code
    if (low !in 0xDC00..0xDFFF) {
        return malformed(0, index, throwOnMalformed)
    }
    return 0x10000 + ((high and 0x3FF) shl 10) or (low and 0x3FF)
}

/**
 * Returns code point corresponding to UTF-8 sequence of two bytes,
 * where the first byte of the sequence is the [byte1] and the second byte is in the [bytes] array at the [index].
 * Returns zero if the sequence is malformed and [throwOnMalformed] is false.
 *
 * @throws CharacterCodingException if the sequence of two bytes is malformed and [throwOnMalformed] is true.
 */
private fun codePointFrom2(bytes: ByteArray, byte1: Int, index: Int, endIndex: Int, throwOnMalformed: Boolean): Int {
    if (byte1 and 0x1E == 0 || index >= endIndex) {
        return malformed(0, index, throwOnMalformed)
    }
    val byte2 = bytes[index].toInt()
    if (byte2 and 0xC0 != 0x80) {
        return malformed(0, index, throwOnMalformed)
    }
    return (byte1 shl 6) xor byte2 xor 0xF80
}

/**
 * Returns code point corresponding to UTF-8 sequence of three bytes,
 * where the first byte of the sequence is the [byte1] and the others are in the [bytes] array starting from the [index].
 * Returns a non-positive value indicating number of bytes from [bytes] included in malformed sequence
 * if the sequence is malformed and [throwOnMalformed] is false.
 *
 * @throws CharacterCodingException if the sequence of three bytes is malformed and [throwOnMalformed] is true.
 */
private fun codePointFrom3(bytes: ByteArray, byte1: Int, index: Int, endIndex: Int, throwOnMalformed: Boolean): Int {
    if (index >= endIndex) {
        return malformed(0, index, throwOnMalformed)
    }

    val byte2 = bytes[index].toInt()
    if (byte1 and 0xF == 0) {
        if (byte2 and 0xE0 != 0xA0) {
            // Non-shortest form
            return malformed(0, index, throwOnMalformed)
        }
    } else if (byte1 and 0xF == 0xD) {
        if (byte2 and 0xE0 != 0x80) {
            // Surrogate code point
            return malformed(0, index, throwOnMalformed)
        }
    } else if (byte2 and 0xC0 != 0x80) {
        return malformed(0, index, throwOnMalformed)
    }

    if (index + 1 == endIndex) {
        return malformed(1, index, throwOnMalformed)
    }
    val byte3 = bytes[index + 1].toInt()
    if (byte3 and 0xC0 != 0x80) {
        return malformed(1, index, throwOnMalformed)
    }

    return (byte1 shl 12) xor (byte2 shl 6) xor byte3 xor -0x1E080
}

/**
 * Returns code point corresponding to UTF-8 sequence of four bytes,
 * where the first byte of the sequence is the [byte1] and the others are in the [bytes] array starting from the [index].
 * Returns a non-positive value indicating number of bytes from [bytes] included in malformed sequence
 * if the sequence is malformed and [throwOnMalformed] is false.
 *
 * @throws CharacterCodingException if the sequence of four bytes is malformed and [throwOnMalformed] is true.
 */
private fun codePointFrom4(bytes: ByteArray, byte1: Int, index: Int, endIndex: Int, throwOnMalformed: Boolean): Int {
    if (index >= endIndex) {
        return malformed(0, index, throwOnMalformed)
    }

    val byte2 = bytes[index].toInt()
    if (byte1 and 0xF == 0x0) {
        if (byte2 and 0xF0 <= 0x80) {
            // Non-shortest form
            return malformed(0, index, throwOnMalformed)
        }
    } else if (byte1 and 0xF == 0x4) {
        if (byte2 and 0xF0 != 0x80) {
            // Out of Unicode code points domain (larger than U+10FFFF)
            return malformed(0, index, throwOnMalformed)
        }
    } else if (byte1 and 0xF > 0x4) {
        return malformed(0, index, throwOnMalformed)
    }

    if (byte2 and 0xC0 != 0x80) {
        return malformed(0, index, throwOnMalformed)
    }

    if (index + 1 == endIndex) {
        return malformed(1, index, throwOnMalformed)
    }
    val byte3 = bytes[index + 1].toInt()
    if (byte3 and 0xC0 != 0x80) {
        return malformed(1, index, throwOnMalformed)
    }

    if (index + 2 == endIndex) {
        return malformed(2, index, throwOnMalformed)
    }
    val byte4 = bytes[index + 2].toInt()
    if (byte4 and 0xC0 != 0x80) {
        return malformed(2, index, throwOnMalformed)
    }
    return (byte1 shl 18) xor (byte2 shl 12) xor (byte3 shl 6) xor byte4 xor 0x381F80
}

/**
 * Maximum number of bytes needed to encode a single char.
 *
 * Code points in `0..0x7F` are encoded in a single byte.
 * Code points in `0x80..0x7FF` are encoded in two bytes.
 * Code points in `0x800..0xD7FF` or in `0xE000..0xFFFF` are encoded in three bytes.
 * Surrogate code points in `0xD800..0xDFFF` are not Unicode scalar values, therefore aren't encoded.
 * Code points in `0x10000..0x10FFFF` are represented by a pair of surrogate `Char`s and are encoded in four bytes.
 */
private const val MAX_BYTES_PER_CHAR = 3

private fun utf8Size(string: String, start: Int, end: Int, strict: Boolean, prefixBytes: Int): Int {
    var count = prefixBytes.toLong()
    var index = start
    while (index < end) {
        val code = string[index++].code
        count += when {
            code < 0x80 -> 1
            code < 0x800 -> 2
            code in 0xD800..0xDFFF && codePointFromSurrogate(string, code, index, end, strict) != 0 -> {
                index++
                4
            }
            else -> 3
        }
    }
    if (count > System.Array.MaxLength) throw System.OutOfMemoryException()
    return count.toInt()
}

/**
 * The byte sequence a malformed UTF-16 char sequence is replaced by.
 */
private val REPLACEMENT_BYTE_SEQUENCE: ByteArray = byteArrayOf(0xEF.toByte(), 0xBF.toByte(), 0xBD.toByte())

/**
 * Encodes the [string] using UTF-8 and returns the resulting [ByteArray].
 *
 * @param string the string to encode.
 * @param startIndex the start offset (inclusive) of the substring to encode.
 * @param endIndex the end offset (exclusive) of the substring to encode.
 * @param throwOnMalformed whether to throw on malformed char sequence or replace by the [REPLACEMENT_BYTE_SEQUENCE].
 *
 * @throws CharacterCodingException if the char sequence is malformed and [throwOnMalformed] is true.
 */
internal fun encodeUtf8(string: String, startIndex: Int, endIndex: Int, throwOnMalformed: Boolean): ByteArray {
    AbstractList.checkBoundsIndexes(startIndex, endIndex, string.length)

    val length = endIndex - startIndex
    if (length == 0) return ByteArray(0)
    var charIndex = startIndex
    var byteIndex = 0
    var bytes: ByteArray
    if (length <= System.Array.MaxLength / MAX_BYTES_PER_CHAR) {
        // The count sizes an optimistic buffer. Only FromUtf16's valid prefix is committed.
        val source = System.MemoryExtensions.AsSpan(string, startIndex, length)
        val candidate = ByteArray(System.Text.Encoding.UTF8.GetByteCount(string, startIndex, length))
        var read = 0
        var written = 0
        val dest = MemoryMarshal.CreateSpan<UByte>(byref(candidate.asUByteArray()[0]), candidate.size)
        Utf8.FromUtf16(source, dest, byref(read), byref(written), false, true)
        if (read == length && written == candidate.size) return candidate
        charIndex += read
        byteIndex = written
        bytes = candidate
    } else {
        // A large ASCII result can fit even if the worst-case byte count would overflow.
        bytes = ByteArray(utf8Size(string, startIndex, endIndex, throwOnMalformed, 0))
    }

    while (charIndex < endIndex) {
        // The optimistic count is not authoritative for malformed input. Only size the
        // remaining suffix if the buffer can no longer fit a whole Unicode scalar.
        if (bytes.size - byteIndex < 4) {
            val size = utf8Size(string, charIndex, endIndex, throwOnMalformed, byteIndex)
            if (size > bytes.size) bytes = bytes.copyOf(size)
        }
        val code = string[charIndex++].code
        when {
            code < 0x80 ->
                bytes[byteIndex++] = code.toByte()
            code < 0x800 -> {
                bytes[byteIndex++] = ((code shr 6) or 0xC0).toByte()
                bytes[byteIndex++] = ((code and 0x3F) or 0x80).toByte()
            }
            code < 0xD800 || code >= 0xE000 -> {
                bytes[byteIndex++] = ((code shr 12) or 0xE0).toByte()
                bytes[byteIndex++] = (((code shr 6) and 0x3F) or 0x80).toByte()
                bytes[byteIndex++] = ((code and 0x3F) or 0x80).toByte()
            }
            else -> { // Surrogate char value
                val codePoint = codePointFromSurrogate(string, code, charIndex, endIndex, throwOnMalformed)
                if (codePoint <= 0) {
                    bytes[byteIndex++] = REPLACEMENT_BYTE_SEQUENCE[0]
                    bytes[byteIndex++] = REPLACEMENT_BYTE_SEQUENCE[1]
                    bytes[byteIndex++] = REPLACEMENT_BYTE_SEQUENCE[2]
                } else {
                    bytes[byteIndex++] = ((codePoint shr 18) or 0xF0).toByte()
                    bytes[byteIndex++] = (((codePoint shr 12) and 0x3F) or 0x80).toByte()
                    bytes[byteIndex++] = (((codePoint shr 6) and 0x3F) or 0x80).toByte()
                    bytes[byteIndex++] = ((codePoint and 0x3F) or 0x80).toByte()
                    charIndex++
                }
            }
        }
    }

    return if (byteIndex == bytes.size) bytes else bytes.copyOf(byteIndex)
}

/**
 * The character a malformed UTF-8 byte sequence is replaced by.
 */
private const val REPLACEMENT_CHAR = '\uFFFD'

// Both callers pass the capacity that created this writable span (String.Create or the
// exact CharArray length). Check it before taking any managed reference, even for empty spans.
private fun putUtf16(target: Span<Char>, offset: Int, capacity: Int, unit: Char) {
    if (offset >= 0 && offset < capacity) {
        var slot by byref(System.Runtime.CompilerServices.Unsafe.Add<Char>(
            byref(MemoryMarshal.GetReference<Char>(target)), offset))
        slot = unit
    }
}

/**
 * Decodes the UTF-8 [bytes] array and returns the resulting [String].
 *
 * @param bytes the byte array to decode.
 * @param startIndex the start offset (inclusive) of the array to be decoded.
 * @param endIndex the end offset (exclusive) of the array to be encoded.
 * @param throwOnMalformed whether to throw on malformed byte sequence or replace by the [REPLACEMENT_CHAR].
 *
 * @throws CharacterCodingException if the array is malformed UTF-8 byte sequence and [throwOnMalformed] is true.
 */
internal fun decodeUtf8(bytes: ByteArray, startIndex: Int, endIndex: Int, throwOnMalformed: Boolean): String {
    AbstractList.checkBoundsIndexes(startIndex, endIndex, bytes.size)

    val length = endIndex - startIndex
    if (length == 0) return ""
    val unsigned = bytes.asUByteArray()
    val capacity = System.Text.Encoding.UTF8.GetCharCount(unsigned, startIndex, length)
    var actual = 0
    val candidate = System.String.Create<Int>(capacity, length) { target, count ->
        val source = MemoryMarshal.CreateReadOnlySpan<UByte>(byref(unsigned[startIndex]), count)
        var read = 0
        var written = 0
        Utf8.ToUtf16(source, target, byref(read), byref(written), false, true)
        actual = if (read == count) written else decodeUtf8Tail(bytes, startIndex + read, endIndex,
            throwOnMalformed, target, capacity, written)
    }
    if (actual == capacity) return candidate
    if (actual < capacity) return candidate.substring(0, actual)
    // Neither the native estimate nor the contents of a mutable byte array are promises.
    // Each consumed byte can produce at most one UTF-16 unit, including replacement.
    val chars = CharArray(length)
    val span = MemoryMarshal.CreateSpan<Char>(byref(chars[0]), length)
    val used = decodeUtf8Tail(bytes, startIndex, endIndex, throwOnMalformed, span, length, 0)
    return chars.concatToString(0, used)
}

private fun decodeUtf8Tail(bytes: ByteArray, startIndex: Int, endIndex: Int, throwOnMalformed: Boolean,
                           target: Span<Char>, capacity: Int, prefix: Int): Int {
    var byteIndex = startIndex
    var out = prefix

    while (byteIndex < endIndex) {
        val byte = bytes[byteIndex++].toInt()
        when {
            byte >= 0 ->
                putUtf16(target, out++, capacity, byte.toChar())
            byte shr 5 == -2 -> {
                val code = codePointFrom2(bytes, byte, byteIndex, endIndex, throwOnMalformed)
                if (code <= 0) {
                    putUtf16(target, out++, capacity, REPLACEMENT_CHAR)
                    byteIndex += -code
                } else {
                    putUtf16(target, out++, capacity, code.toChar())
                    byteIndex += 1
                }
            }
            byte shr 4 == -2 -> {
                val code = codePointFrom3(bytes, byte, byteIndex, endIndex, throwOnMalformed)
                if (code <= 0) {
                    putUtf16(target, out++, capacity, REPLACEMENT_CHAR)
                    byteIndex += -code
                } else {
                    putUtf16(target, out++, capacity, code.toChar())
                    byteIndex += 2
                }
            }
            byte shr 3 == -2 -> {
                val code = codePointFrom4(bytes, byte, byteIndex, endIndex, throwOnMalformed)
                if (code <= 0) {
                    putUtf16(target, out++, capacity, REPLACEMENT_CHAR)
                    byteIndex += -code
                } else {
                    val high = (code - 0x10000) shr 10 or 0xD800
                    val low = (code and 0x3FF) or 0xDC00
                    putUtf16(target, out++, capacity, high.toChar())
                    putUtf16(target, out++, capacity, low.toChar())
                    byteIndex += 3
                }
            }
            else -> {
                val _ = malformed(0, byteIndex, throwOnMalformed)
                putUtf16(target, out++, capacity, REPLACEMENT_CHAR)
            }
        }
    }

    return out
}
