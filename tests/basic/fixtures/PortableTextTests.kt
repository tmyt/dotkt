@file:Suppress("DEPRECATION")

import NUnit.Framework.TestAttribute

private class PortableTextSequence(private val text: String) : CharSequence {
    override val length: Int get() = text.length
    override fun get(index: Int): Char = text[index]
    override fun subSequence(startIndex: Int, endIndex: Int): CharSequence = text.substring(startIndex, endIndex)
    override fun toString(): String = text
}

class PortableTextTests {
    @TestAttribute fun characterReplacementKeepsOrdinalAndCharacterWiseCaseRules() {
        check("abABab".replace('a', 'x') == "xbABxb")
        check("abABab".replace('a', 'x', true) == "xbxBxb")
        check("\u212AKk".replace('k', '!', true) == "!!!")
        val characters = charArrayOf(0.toChar(), 'a', 'A', 'é', 'É', 0x212a.toChar(), 'k', '?', 0xd800.toChar(), 0xdfff.toChar())
        val source = characters.concatToString()
        val replacement: (String, Char, Char, Boolean) -> String = String::replace
        for (old in characters) for (new in characters) for (ignoreCase in listOf(false, true)) {
            val expected = CharArray(source.length) { index ->
                if (source[index].equals(old, ignoreCase)) new else source[index]
            }.concatToString()
            check(source.replace(old, new, ignoreCase) == expected) { "direct old=${old.code} new=${new.code} ignoreCase=$ignoreCase" }
            check(replacement(source, old, new, ignoreCase) == expected) { "reference old=${old.code} new=${new.code} ignoreCase=$ignoreCase" }
        }
    }

    @TestAttribute fun substringReplacementHandlesEmptyAdjacentAndLiteralMatches() {
        check("aaaa".replace("aa", "x") == "xx")
        check("ababa".replace("aba", "X") == "Xba")
        check("banana".replace("na", "") == "ba")
        check("ab".replace("", "_") == "_a_b_")
        check("".replace("", "_") == "_")
        check("".replace("x", "y") == "")
        check("abc".replace("abcdef", "x") == "abc")
        check("oneONE".replace("one", "x", true) == "xx")
        check("\u212AKk".replace("k", "!", true) == "!!!")
        check("a\u0000a".replace("\u0000", "|") == "a|a")
        check("a.a".replace(".", "$1") == "a$1a")
        val replacement: (String, String, String, Boolean) -> String = String::replace
        check(replacement("ababa", "ba", "!", false) == "a!!")
    }

    @TestAttribute fun firstReplacementChangesOnlyTheFirstMatchingRange() {
        check("aaaa".replaceFirst("aa", "x") == "xaa")
        check("ababa".replaceFirst("aba", "X") == "Xba")
        check("ab".replaceFirst("", "_") == "_ab")
        check("".replaceFirst("", "_") == "_")
        check("abc".replaceFirst("x", "y") == "abc")
        check("oneONE".replaceFirst("ONE", "x", true) == "xONE")
        check("abABab".replaceFirst('A', 'x', true) == "xbABab")
        check("abc".replaceFirst('x', 'y') == "abc")
        val firstChar: (String, Char, Char, Boolean) -> String = String::replaceFirst
        val firstText: (String, String, String, Boolean) -> String = String::replaceFirst
        check(firstChar("\u212AKk", 'k', '!', true) == "!Kk")
        check(firstText("a.a", ".", "$1", false) == "a$1a")
    }

    @TestAttribute fun repetitionUsesPortablePolicyForStringsAndCustomSequences() {
        val repetition: (CharSequence, Int) -> String = CharSequence::repeat
        fun verify(value: CharSequence) {
            check(value.repeat(0) == "")
            check(value.repeat(1) == "ab")
            check(value.repeat(2) == "abab")
            check(repetition(value, 3) == "ababab")
            for (count in intArrayOf(Int.MIN_VALUE, -1)) {
                var rejected = false
                try { value.repeat(count) } catch (_: IllegalArgumentException) { rejected = true }
                check(rejected)
            }
        }
        verify("ab")
        verify(PortableTextSequence("ab"))
        verify(StringBuilder("ab"))
        check(PortableTextSequence("").repeat(17) == "")
        check("Aé".repeat(3) == "AéAéAé")
    }

    @TestAttribute fun firstCharacterCasingRetainsTheNativeCharPrimitivePolicy() {
        val cases = listOf("", "hello", "HELLO", "éclair", "Éclair", "ßtail", "\u0130tail", "\u0131tail", "\u01c5tail", System.Char.ConvertFromUtf32(0x10400) + "tail")
        val capitalize: (String) -> String = String::capitalize
        val decapitalize: (String) -> String = String::decapitalize
        for (value in cases) {
            val upper = if (value.isEmpty()) "" else value[0].uppercaseChar().toString() + value.substring(1)
            val lower = if (value.isEmpty()) "" else value[0].lowercaseChar().toString() + value.substring(1)
            check(value.capitalize() == upper && capitalize(value) == upper)
            check(value.decapitalize() == lower && decapitalize(value) == lower)
        }
    }

    @TestAttribute fun isoControlClassificationCoversEveryUtf16CodeUnit() {
        val classify: (Char) -> Boolean = Char::isISOControl
        for (code in 0..0xffff) {
            val expected = code <= 0x1f || code in 0x7f..0x9f
            check(code.toChar().isISOControl() == expected)
            check(classify(code.toChar()) == expected)
        }
    }
}
