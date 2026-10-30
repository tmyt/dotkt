import NUnit.Framework.TestAttribute

private fun textBoundsKind(action: () -> Unit): Int {
    try { action() }
    catch (_: IndexOutOfBoundsException) { return 1 }
    catch (_: IllegalArgumentException) { return 2 }
    catch (_: CharacterCodingException) { return 3 }
    return 0
}

private fun textBoundsExpected(start: Int, end: Int, size: Int): Int = when {
    start < 0 || end > size -> 1
    start > end -> 2
    else -> 0
}

class TextBoundsTests {
    @TestAttribute fun validSlicesKeepNativeCharactersAndUtf8Payloads() {
        val text = "Aé" + System.Char.ConvertFromUtf32(0x1f600) + "Z"
        val chars = text.toCharArray()
        check(chars.concatToString() == text)
        for (start in 0..text.length) for (end in start..text.length) {
            val expected = text.substring(start, end)
            check(chars.concatToString(start, end) == expected)
            check(text.toCharArray(start, end).concatToString() == expected)
            check(text.encodeToByteArray(start, end).contentEquals(expected.encodeToByteArray()))
            val destination = CharArray(text.length + 2) { '#' }
            check(text.toCharArray(destination, 1, start, end) === destination)
            for (index in destination.indices) {
                val value = if (index >= 1 && index < 1 + end - start) text[start + index - 1] else '#'
                check(destination[index] == value)
            }
        }
        val bytes = "abcd".encodeToByteArray()
        for (start in 0..4) for (end in start..4) {
            check(bytes.decodeToString(start, end) == "abcd".substring(start, end))
        }
        check(text.toCharArray(0, text.length) !== chars)
    }

    @TestAttribute fun charSlicesUseUpstreamBoundsOrderingAtIntegerExtremes() {
        val text = "abcd"
        val chars = text.toCharArray()
        val indexes = intArrayOf(Int.MIN_VALUE, -1, 0, 1, 4, 5, Int.MAX_VALUE)
        for (start in indexes) for (end in indexes) {
            val expected = textBoundsExpected(start, end, 4)
            check(textBoundsKind { chars.concatToString(start, end) } == expected) { "concat start=$start end=$end" }
            check(textBoundsKind { text.toCharArray(start, end) } == expected) { "chars start=$start end=$end" }
        }
        check(charArrayOf().concatToString(0, 0) == "")
        check("".toCharArray(0, 0).isEmpty())
        check(textBoundsKind { chars.concatToString(2, 1) } == 2)
    }

    @TestAttribute fun destinationChecksAreOverflowSafeAndRejectBeforeMutation() {
        val text = "abc"
        val indexes = intArrayOf(Int.MIN_VALUE, -1, 0, 1, 3, 4, Int.MAX_VALUE)
        val offsets = intArrayOf(Int.MIN_VALUE, -1, 0, 1, 4, 5, Int.MAX_VALUE)
        for (start in indexes) for (end in indexes) for (offset in offsets) {
            val destination = charArrayOf('#', '#', '#', '#')
            val sourceError = textBoundsExpected(start, end, text.length)
            val expected = if (sourceError != 0) sourceError else if (
                offset < 0 || offset > destination.size || end - start > destination.size - offset
            ) 1 else 0
            var result: CharArray? = null
            val actual = textBoundsKind { result = text.toCharArray(destination, offset, start, end) }
            check(actual == expected) { "copy start=$start end=$end offset=$offset" }
            if (expected != 0) {
                check(destination.contentEquals(charArrayOf('#', '#', '#', '#')))
            } else {
                check(result === destination)
                for (index in destination.indices) {
                    val value = if (index >= offset && index < offset + end - start) text[start + index - offset] else '#'
                    check(destination[index] == value)
                }
            }
        }
        val empty = charArrayOf()
        check(text.toCharArray(empty, 0, 1, 1) === empty)
    }

    @TestAttribute fun utf8RangeValidationPrecedesCodingAndKeepsBothModes() {
        val text = "abcd"
        val bytes = text.encodeToByteArray()
        val indexes = intArrayOf(Int.MIN_VALUE, -1, 0, 1, 4, 5, Int.MAX_VALUE)
        for (start in indexes) for (end in indexes) for (strict in listOf(false, true)) {
            val expected = textBoundsExpected(start, end, 4)
            check(textBoundsKind { bytes.decodeToString(start, end, strict) } == expected)
            check(textBoundsKind { text.encodeToByteArray(start, end, strict) } == expected)
        }
        val malformed = byteArrayOf(0xc3.toByte(), 0x28)
        check(malformed.decodeToString() == "\uFFFD(")
        check(malformed.decodeToString(0, 2, false) == "\uFFFD(")
        check(textBoundsKind { malformed.decodeToString(0, 2, true) } == 3)
        check(textBoundsKind { malformed.decodeToString(-1, 2, true) } == 1)
        check(textBoundsKind { malformed.decodeToString(1, 0, true) } == 2)
        val unpaired = 0xd800.toChar().toString()
        check(unpaired.encodeToByteArray().contentEquals(byteArrayOf(0xef.toByte(), 0xbf.toByte(), 0xbd.toByte())))
        check(textBoundsKind { unpaired.encodeToByteArray(0, 1, true) } == 3)
        check(textBoundsKind { unpaired.encodeToByteArray(-1, 1, true) } == 1)
        check(textBoundsKind { unpaired.encodeToByteArray(1, 0, true) } == 2)
        check(byteArrayOf().decodeToString(0, 0, true) == "")
        check("".encodeToByteArray(0, 0, true).isEmpty())
    }

    @TestAttribute @OptIn(ExperimentalUnsignedTypes::class)
    fun referencesDefaultsAndNativeArrayAliasesKeepTheirContracts() {
        val concatenate: (CharArray, Int, Int) -> String = CharArray::concatToString
        val slice: (String, Int, Int) -> CharArray = String::toCharArray
        val copy: (String, CharArray, Int, Int, Int) -> CharArray = String::toCharArray
        val encode: (String, Int, Int, Boolean) -> ByteArray = String::encodeToByteArray
        val decode: (ByteArray, Int, Int, Boolean) -> String = ByteArray::decodeToString
        check(concatenate(charArrayOf('a', 'b', 'c'), 1, 3) == "bc")
        check(slice("abc", 1, 3).concatToString() == "bc")
        val destination = charArrayOf('#', '#', '#', '#')
        check(copy("abc", destination, 0, 1, 3) === destination)
        check(destination.concatToString() == "bc##")
        check(System.Convert.ToBase64CharArray(ubyteArrayOf(97u, 98u, 99u), 0, 3, destination, 0) == 4)
        check(destination.concatToString() == "YWJj")
        check("abc".toCharArray(destination) === destination && destination.concatToString() == "abcj")
        check(decode(encode("abcd", 1, 3, true), 0, 2, true) == "bc")
        check(textBoundsKind { copy("abc", destination, Int.MAX_VALUE, 0, 1) } == 1)
        check(textBoundsKind { concatenate(destination, 2, 1) } == 2)
    }
}
