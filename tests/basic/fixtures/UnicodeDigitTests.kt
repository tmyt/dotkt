import NUnit.Framework.TestAttribute

private fun unicodeDigitRejectsNumber(action: () -> Unit) {
    var rejected = false
    try { action() } catch (_: NumberFormatException) { rejected = true }
    check(rejected)
}

private fun unicodeDigitRejectsArgument(action: () -> Unit) {
    var kind = 0
    try { action() }
    catch (_: NumberFormatException) { kind = 1 }
    catch (_: IllegalArgumentException) { kind = 2 }
    check(kind == 2)
}

private fun unicodeDecimalText(text: String, zero: Int): String =
    CharArray(text.length) { index ->
        val c = text[index]
        if (c in '0'..'9') (zero + (c - '0')).toChar() else c
    }.concatToString()

class UnicodeDigitTests {
    @TestAttribute fun everyBmpCharacterAndRadixMatchesIndependentNativeValues() {
        var decimals = 0
        for (code in 0..65535) {
            val c = code.toChar()
            // Independent CLR Unicode data, not the stdlib's generated lookup table.
            val decimal = if (System.Char.IsDigit(c)) System.Char.GetNumericValue(c).toInt() else -1
            check(c.isDigit() == (decimal >= 0))
            if (decimal >= 0) decimals++
            check(c.digitToIntOrNull() == if (decimal >= 0) decimal else null) { "decimal U+$code" }
            val digit = when (code) {
                in 65..90 -> code - 65 + 10
                in 97..122 -> code - 97 + 10
                in 0xff21..0xff3a -> code - 0xff21 + 10
                in 0xff41..0xff5a -> code - 0xff41 + 10
                else -> decimal
            }
            for (radix in 2..36) {
                val expected = if (digit >= 0 && digit < radix) digit else null
                check(c.digitToIntOrNull(radix) == expected) { "U+$code radix=$radix" }
            }
        }
        check(decimals == 370)
    }

    @TestAttribute fun everyDecimalAlphabetUsesCommonParserBounds() {
        var alphabets = 0
        for (code in 0..65535) {
            val c = code.toChar()
            if (!System.Char.IsDigit(c) || System.Char.GetNumericValue(c) != 0.0) continue
            alphabets++
            check(unicodeDecimalText("-128", code).toByte() == Byte.MIN_VALUE)
            check(unicodeDecimalText("32767", code).toShort() == Short.MAX_VALUE)
            check(unicodeDecimalText("+2147483647", code).toInt() == Int.MAX_VALUE)
            check(unicodeDecimalText("-2147483648", code).toInt() == Int.MIN_VALUE)
            check(unicodeDecimalText("+9223372036854775807", code).toLong() == Long.MAX_VALUE)
            check(unicodeDecimalText("-9223372036854775808", code).toLong() == Long.MIN_VALUE)
            check(unicodeDecimalText("255", code).toUByte() == UByte.MAX_VALUE)
            check(unicodeDecimalText("65535", code).toUShort() == UShort.MAX_VALUE)
            check(unicodeDecimalText("4294967295", code).toUInt() == UInt.MAX_VALUE)
            check(unicodeDecimalText("18446744073709551615", code).toULong() == ULong.MAX_VALUE)
            check(unicodeDecimalText("2147483648", code).toIntOrNull() == null)
            check(unicodeDecimalText("9223372036854775808", code).toLongOrNull() == null)
            check(unicodeDecimalText("4294967296", code).toUIntOrNull() == null)
            check(unicodeDecimalText("18446744073709551616", code).toULongOrNull() == null)
            unicodeDigitRejectsNumber { unicodeDecimalText("128", code).toByte() }
            unicodeDigitRejectsNumber { unicodeDecimalText("-32769", code).toShort() }
            unicodeDigitRejectsNumber { unicodeDecimalText("-1", code).toUInt() }
        }
        check(alphabets == 37)
    }

    @TestAttribute fun fullwidthLettersAndMixedAlphabetsParseInEveryRadix() {
        for (radix in 2..36) {
            for (digit in 0 until radix) {
                val upper = if (digit < 10) (0xff10 + digit).toChar() else (0xff21 + digit - 10).toChar()
                val lower = if (digit < 10) (0x0660 + digit).toChar() else (0xff41 + digit - 10).toChar()
                check(upper.digitToInt(radix) == digit && lower.digitToInt(radix) == digit)
                check(upper.toString().toInt(radix) == digit && lower.toString().toUInt(radix) == digit.toUInt())
            }
        }
        check("Ｆｆ".toInt(16) == 255)
        check("ｚＺ".toLong(36) == 1295L)
        check("١２३".toInt() == 123)
        check("-١２३".toLong() == -123L)
        check("+١２३".toULong() == 123uL)
        check("ＦＦＦＦＦＦＦＦ".toUInt(16) == UInt.MAX_VALUE)
        check("ｆｆｆｆｆｆｆｆｆｆｆｆｆｆｆｆ".toULong(16) == ULong.MAX_VALUE)
    }

    @TestAttribute fun invalidCharactersAndRadicesKeepTheirExceptionKinds() {
        for (code in intArrayOf(0, 31, 47, 58, 127, 128, 0x065f, 0x066a, 0x00b2, 0x2165, 0xd800, 0xdfff, 0xff0f, 0xff1a, 0xff20, 0xff3b, 0xffff)) {
            val c = code.toChar()
            check(c.digitToIntOrNull() == null && c.digitToIntOrNull(36) == null)
            unicodeDigitRejectsArgument { c.digitToInt() }
            unicodeDigitRejectsArgument { c.digitToInt(36) }
            unicodeDigitRejectsNumber { c.toString().toInt() }
        }
        for (radix in intArrayOf(Int.MIN_VALUE, -1, 0, 1, 37, Int.MAX_VALUE)) {
            for (c in charArrayOf('0', '١', 'Ａ', '!')) {
                unicodeDigitRejectsArgument { c.digitToInt(radix) }
                unicodeDigitRejectsArgument { c.digitToIntOrNull(radix) }
                unicodeDigitRejectsArgument { c.toString().toInt(radix) }
                unicodeDigitRejectsArgument { c.toString().toULong(radix) }
            }
        }
        for (text in listOf(" ١", "١ ", "١_٢", "--١", "＋١", "−١")) {
            check(text.toIntOrNull() == null && text.toULongOrNull() == null)
            unicodeDigitRejectsNumber { text.toInt() }
        }
        check('z'.digitToIntOrNull(35) == null && 'ｚ'.digitToIntOrNull(35) == null)
    }

    @TestAttribute fun referencesAndNativeCharInteropKeepTheirTypes() {
        val decimal: (Char) -> Int = Char::digitToInt
        val radix: (Char, Int) -> Int? = Char::digitToIntOrNull
        val bound: (Int) -> Int = 'Ｆ'::digitToInt
        check(decimal('١') == 1 && radix('ｚ', 36) == 35 && bound(16) == 15)
        check(radix('Ｆ', 15) == null)
        val c = System.Char.Parse("١")
        check(c.digitToInt() == 1 && System.Char.GetNumericValue(c) == 1.0)
        check(c.code == 0x0661 && c.category == CharCategory.DECIMAL_DIGIT_NUMBER)
        check('a'.uppercaseChar() == 'A' && ' '.isWhitespace())
        check(0xd800.toChar().isHighSurrogate() && 0xdc00.toChar().isLowSurrogate())
    }
}
