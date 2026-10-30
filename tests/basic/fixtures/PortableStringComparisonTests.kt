import NUnit.Framework.TestAttribute

private fun portableComparisonFold(value: String): String = CharArray(value.length) { index ->
    System.Char.ToLowerInvariant(System.Char.ToUpperInvariant(value[index]))
}.concatToString()

private fun portableComparisonSign(value: Int): Int = when {
    value < 0 -> -1
    value > 0 -> 1
    else -> 0
}

private fun portableComparisonRegion(value: String, offset: Int, part: String, ignoreCase: Boolean): Boolean {
    if (offset < 0 || offset > value.length || part.length > value.length - offset) return false
    for (index in part.indices) {
        val left = value[offset + index]
        val right = part[index]
        if (ignoreCase) {
            val a = System.Char.ToLowerInvariant(System.Char.ToUpperInvariant(left))
            val b = System.Char.ToLowerInvariant(System.Char.ToUpperInvariant(right))
            if (a != b) return false
        } else if (left != right) return false
    }
    return true
}

class PortableStringComparisonTests {
    @TestAttribute fun everyBmpCaseClassAgreesAcrossComparisonAndRegions() {
        val order = String.CASE_INSENSITIVE_ORDER
        for (code in 0..65535) {
            val value = code.toChar().toString()
            val folded = portableComparisonFold(value)
            check(value.equals(folded, ignoreCase = true)) { "equals U+$code" }
            check(value.compareTo(folded, ignoreCase = true) == 0) { "compare U+$code" }
            check(order.compare(value, folded) == 0)
            check((value + "!").startsWith(folded, ignoreCase = true))
            check(("!" + value).startsWith(folded, 1, ignoreCase = true))
            check(("!" + value).endsWith(folded, ignoreCase = true))
            val other = ((code + 1) and 65535).toChar().toString()
            val expected = System.String.CompareOrdinal(folded, portableComparisonFold(other))
            check(portableComparisonSign(value.compareTo(other, true)) == portableComparisonSign(expected))
        }
    }

    @TestAttribute fun nullableEmptyAndUnicodePairsUseCharacterWiseFolding() {
        val values = listOf("", "K", "k", "K", "S", "s", "ſ", "I", "i", "ı", "İ", "Σ", "σ", "ς", "ß", "ss", "ẞ", "ab", "aB", "KΣ", "kς", "İx", "ix", "a!", "a")
        for (a in values) for (b in values) {
            val left: String? = a
            val right: String? = b
            val expected = portableComparisonFold(a) == portableComparisonFold(b)
            check(left.equals(right, true) == expected)
            check(left.equals(right, false) == (a == b))
            val ordinal = System.String.CompareOrdinal(portableComparisonFold(a), portableComparisonFold(b))
            check(portableComparisonSign(a.compareTo(b, true)) == portableComparisonSign(ordinal))
        }
        val missing: String? = null
        check(missing.equals(null, true) && missing.equals(null, false))
        check(!missing.equals("", true) && !"".equals(missing, true))
        check(!"ß".equals("ss", true))
        check("K".equals("k", true) && "K".compareTo("k", true) == 0)
        val supplementaryUpper = System.Char.ConvertFromUtf32(0x10400)
        val supplementaryLower = System.Char.ConvertFromUtf32(0x10428)
        check(supplementaryUpper.uppercase() == supplementaryLower.uppercase())
        check(!supplementaryUpper.equals(supplementaryLower, true))
        check(!supplementaryUpper.contentEquals(supplementaryLower, true))
        check(supplementaryUpper.compareTo(supplementaryLower, true) < 0)
        check(!supplementaryUpper.startsWith(supplementaryLower, true))
        check(!supplementaryUpper.endsWith(supplementaryLower, true))
        check(!supplementaryUpper.regionMatches(0, supplementaryLower, 0, 2, true))
    }

    @TestAttribute fun prefixSuffixAndOffsetBoundsAgreeWithIndependentRegions() {
        for (value in listOf("", "ab", "xKΣy", "ſı", "a!")) {
            for (part in listOf("", "a", "b", "kσ", "KΣ", "SI", "!", "much longer")) {
                for (ignoreCase in listOf(false, true)) {
                    check(value.startsWith(part, ignoreCase) == portableComparisonRegion(value, 0, part, ignoreCase))
                    check(value.endsWith(part, ignoreCase) == portableComparisonRegion(value, value.length - part.length, part, ignoreCase))
                    for (offset in intArrayOf(Int.MIN_VALUE, -1, 0, 1, value.length, value.length + 1, Int.MAX_VALUE)) {
                        val expected = portableComparisonRegion(value, offset, part, ignoreCase)
                        check(value.startsWith(part, offset, ignoreCase) == expected)
                        check(value.regionMatches(offset, part, 0, part.length, ignoreCase) == expected)
                    }
                }
            }
        }
    }

    @TestAttribute fun ordinalAndNativeStringBoundariesStayUnchanged() {
        val values = listOf("", "a", "A", "z", "ä", "æ", "AE", "K", "k", "ß", "ss", "a-b", "ab")
        for (a in values) for (b in values) {
            val expected = portableComparisonSign(System.String.CompareOrdinal(a, b))
            check(portableComparisonSign(a.compareTo(b)) == expected)
            check(portableComparisonSign(a.compareTo(b, false)) == expected)
        }
        val native = System.String.Concat("K", "Σ")
        check(native.equals("kς", true) && native.startsWith("K", true) && native.endsWith("σ", true))
        check(System.String.CompareOrdinal(native, "kς") != 0)
        check("ß".uppercase() == "ß" && 'a'.uppercaseChar() == 'A')
    }

    @TestAttribute fun referencesAndCaseInsensitiveComparatorKeepTheirContracts() {
        val equals: (String?, String?, Boolean) -> Boolean = String?::equals
        val compare: (String, String, Boolean) -> Int = String::compareTo
        val prefix: (String, String, Boolean) -> Boolean = String::startsWith
        val suffix: (String, String, Boolean) -> Boolean = String::endsWith
        val bound: (String, Boolean) -> Boolean = "Kx"::startsWith
        check(equals(null, null, true) && equals("K", "k", true))
        check(compare("K", "k", true) == 0 && prefix("Kx", "k", true))
        check(suffix("xK", "k", true) && bound("k", true))
        val sorted = listOf("z", "K", "k", "a").sortedWith(String.CASE_INSENSITIVE_ORDER)
        check(sorted == listOf("a", "K", "k", "z"))
    }
}
