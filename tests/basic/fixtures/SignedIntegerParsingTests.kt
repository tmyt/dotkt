import NUnit.Framework.TestAttribute

private fun signedParsingRejectsNumber(action: () -> Unit) {
    var rejected = false
    try { action() } catch (_: NumberFormatException) { rejected = true }
    check(rejected)
}

private fun signedParsingRejectsRadix(action: () -> Unit) {
    var exceptionKind = 0
    try { action() }
    catch (_: NumberFormatException) { exceptionKind = 1 }
    catch (_: IllegalArgumentException) { exceptionKind = 2 }
    check(exceptionKind == 2)
}

class SignedIntegerParsingTests {
    @TestAttribute fun decimalParsersPreserveBoundsAndSignedGrammar() {
        check("-128".toByte() == Byte.MIN_VALUE && "+127".toByte() == Byte.MAX_VALUE)
        check("-32768".toShort() == Short.MIN_VALUE && "+32767".toShort() == Short.MAX_VALUE)
        check("-2147483648".toInt() == Int.MIN_VALUE && "+2147483647".toInt() == Int.MAX_VALUE)
        check("-9223372036854775808".toLong() == Long.MIN_VALUE)
        check("+9223372036854775807".toLong() == Long.MAX_VALUE)
        for (input in listOf("0", "+0", "-0", "0000000000000000000000000000000")) {
            check(input.toByte() == 0.toByte() && input.toShort() == 0.toShort())
            check(input.toInt() == 0 && input.toLong() == 0L)
        }
        for (input in listOf("", "+", "-", " 1", "1 ", "\t1", "1\n", "--1", "+-1", "1_0", "1.0", "1,0", "0x10")) {
            signedParsingRejectsNumber { input.toByte() }
            signedParsingRejectsNumber { input.toShort() }
            signedParsingRejectsNumber { input.toInt() }
            signedParsingRejectsNumber { input.toLong() }
        }
        signedParsingRejectsNumber { "128".toByte() }
        signedParsingRejectsNumber { "-129".toByte() }
        signedParsingRejectsNumber { "32768".toShort() }
        signedParsingRejectsNumber { "-32769".toShort() }
        signedParsingRejectsNumber { "2147483648".toInt() }
        signedParsingRejectsNumber { "-2147483649".toInt() }
        signedParsingRejectsNumber { "9223372036854775808".toLong() }
        signedParsingRejectsNumber { "-9223372036854775809".toLong() }
    }

    @TestAttribute fun radixParsersUseCommonOverflowChecksInEveryBase() {
        for (radix in 2..36) {
            for (value in listOf(Int.MIN_VALUE, -1, 0, 1, Int.MAX_VALUE)) {
                val encoded = value.toString(radix)
                check(encoded.toInt(radix) == value && encoded.toIntOrNull(radix) == value)
            }
            for (value in listOf(Long.MIN_VALUE, -1L, 0L, 1L, Long.MAX_VALUE)) {
                val encoded = value.toString(radix)
                check(encoded.toLong(radix) == value && encoded.toLongOrNull(radix) == value)
            }
            check((-128).toString(radix).toByte(radix) == Byte.MIN_VALUE)
            check(127.toString(radix).toByte(radix) == Byte.MAX_VALUE)
            check((-32768).toString(radix).toShort(radix) == Short.MIN_VALUE)
            check(32767.toString(radix).toShort(radix) == Short.MAX_VALUE)
            check(("+000" + 42.toString(radix)).toInt(radix) == 42)
            signedParsingRejectsNumber { 128.toString(radix).toByte(radix) }
            signedParsingRejectsNumber { (-129).toString(radix).toByte(radix) }
            signedParsingRejectsNumber { 32768.toString(radix).toShort(radix) }
            signedParsingRejectsNumber { (-32769).toString(radix).toShort(radix) }
            signedParsingRejectsNumber { 2147483648L.toString(radix).toInt(radix) }
            signedParsingRejectsNumber { (-2147483649L).toString(radix).toInt(radix) }
            signedParsingRejectsNumber { Long.MIN_VALUE.toString(radix).substring(1).toLong(radix) }
            signedParsingRejectsNumber { (Long.MIN_VALUE.toString(radix) + "0").toLong(radix) }
            val invalid = if (radix < 36) "0123456789abcdefghijklmnopqrstuvwxyz"[radix].toString() else "!"
            signedParsingRejectsNumber { invalid.toInt(radix) }
            signedParsingRejectsNumber { invalid.toLong(radix) }
        }
        check("7FFFFFFF".toInt(16) == Int.MAX_VALUE)
        check("-8000000000000000".toLong(16) == Long.MIN_VALUE)
        signedParsingRejectsNumber { "80000000".toInt(16) }
        signedParsingRejectsNumber { "10000000000000000".toLong(16) }
    }

    @TestAttribute fun invalidRadicesAreCheckedBeforeInputParsing() {
        for (radix in listOf(Int.MIN_VALUE, -1, 0, 1, 37, Int.MAX_VALUE)) {
            for (input in listOf("", "+", "42", "99999999999999999999999999")) {
                signedParsingRejectsRadix { input.toByte(radix) }
                signedParsingRejectsRadix { input.toShort(radix) }
                signedParsingRejectsRadix { input.toInt(radix) }
                signedParsingRejectsRadix { input.toLong(radix) }
            }
            signedParsingRejectsRadix { 42.toString(radix) }
            signedParsingRejectsRadix { 42L.toString(radix) }
        }
    }

    @TestAttribute fun callableReferencesRetainAllSignedOverloads() {
        val byte: (String) -> Byte = String::toByte
        val byteRadix: (String, Int) -> Byte = String::toByte
        val short: (String) -> Short = String::toShort
        val shortRadix: (String, Int) -> Short = String::toShort
        val int: (String) -> Int = String::toInt
        val intRadix: (String, Int) -> Int = String::toInt
        val long: (String) -> Long = String::toLong
        val longRadix: (String, Int) -> Long = String::toLong
        check(byte("-128") == Byte.MIN_VALUE && byteRadix("7f", 16) == Byte.MAX_VALUE)
        check(short("-32768") == Short.MIN_VALUE && shortRadix("7fff", 16) == Short.MAX_VALUE)
        check(int("-2147483648") == Int.MIN_VALUE && intRadix("7fffffff", 16) == Int.MAX_VALUE)
        check(long("-9223372036854775808") == Long.MIN_VALUE)
        check(longRadix("7fffffffffffffff", 16) == Long.MAX_VALUE)
        val bound: (Int) -> Int = "7fffffff"::toInt
        check(bound(16) == Int.MAX_VALUE)
        signedParsingRejectsNumber { byte("128") }
        signedParsingRejectsNumber { longRadix("8000000000000000", 16) }
        signedParsingRejectsRadix { intRadix("", 1) }
    }
}
