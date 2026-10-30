package roundtriptests.unicodedigits

import NUnit.Framework.TestAttribute
import roundtrip.unicodedigits.*

class UnicodeDigitTests {
    @TestAttribute fun digitsAndParsersSurviveLibraryImport() {
        check(decimalDigit('१') == 1 && radixDigit('Ｆ', 16) == 15)
        check(radixDigit('Ｆ', 15) == null)
        check(signedNumber("-９２２３３７２０３６８５４７７５８０８") == Long.MIN_VALUE)
        check(unsignedNumber("ｆｆｆｆｆｆｆｆｆｆｆｆｆｆｆｆ", 16) == ULong.MAX_VALUE)
        val digit = savedDigit()
        check(digit('ｚ', 36) == 35 && digit('!', 36) == null)
        val c = nativeCharacter("١")
        check(c == '١' && nativeDigitValue(c) == 1.0 && c.digitToInt() == 1)
    }

    @TestAttribute fun importedInlineDigitsPreserveSingleEvaluationAndNull() {
        var calls = 0
        check(observedDigit('Ａ', 16) { calls++; it } == 10)
        check(observedDigit('Ａ', 10) { calls++; it } == null)
        check(observedDigit('٥', 10) { calls++; (it ?: 0) + 1 } == 6)
        check(calls == 3)
    }
}
