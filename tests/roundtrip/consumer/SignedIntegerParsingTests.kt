package roundtriptests.signedintegers

import NUnit.Framework.TestAttribute
import roundtrip.signedintegers.*

private fun importedIntegerRejectsNumber(action: () -> Unit) {
    var rejected = false
    try { action() } catch (_: NumberFormatException) { rejected = true }
    check(rejected)
}

class SignedIntegerParsingTests {
    @TestAttribute fun importedParsersRetainWidthsAndOverflowChecks() {
        check(parsedByte("-128") == Byte.MIN_VALUE)
        check(parsedShort("32767") == Short.MAX_VALUE)
        check(parsedInt("-2147483648") == Int.MIN_VALUE)
        check(parsedLong("9223372036854775807") == Long.MAX_VALUE)
        for (radix in 2..36) {
            check(parsedByte((-128).toString(radix), radix) == Byte.MIN_VALUE)
            check(parsedShort(32767.toString(radix), radix) == Short.MAX_VALUE)
            check(parsedInt(Int.MAX_VALUE.toString(radix), radix) == Int.MAX_VALUE)
            check(parsedLong(Long.MIN_VALUE.toString(radix), radix) == Long.MIN_VALUE)
            importedIntegerRejectsNumber { parsedByte(128.toString(radix), radix) }
            importedIntegerRejectsNumber { parsedShort((-32769).toString(radix), radix) }
            importedIntegerRejectsNumber { parsedInt(2147483648L.toString(radix), radix) }
            importedIntegerRejectsNumber { parsedLong(Long.MIN_VALUE.toString(radix).substring(1), radix) }
        }
    }

    @TestAttribute fun importedSavedParsersPreserveExceptionsAndReferences() {
        val decimalByte: (String) -> Byte = ::parsedByte
        val radixShort: (String, Int) -> Short = ::parsedShort
        val radixInt: (String, Int) -> Int = ::parsedInt
        val radixLong = savedLongParser(16)
        check(decimalByte("+127") == Byte.MAX_VALUE)
        check(radixShort("-8000", 16) == Short.MIN_VALUE)
        check(radixInt("7fffffff", 16) == Int.MAX_VALUE)
        check(radixLong("-8000000000000000") == Long.MIN_VALUE)
        importedIntegerRejectsNumber { decimalByte(" 1") }
        importedIntegerRejectsNumber { radixLong("8000000000000000") }
        var invalidRadix = false
        try { parsedLong("", 37) }
        catch (_: NumberFormatException) { error("radix must be checked first") }
        catch (_: IllegalArgumentException) { invalidRadix = true }
        check(invalidRadix)
    }
}
