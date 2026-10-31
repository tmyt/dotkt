import NUnit.Framework.TestAttribute

private class BuilderRangeSequence : CharSequence {
    var slices = 0
    override val length: Int get() = 3
    override fun get(index: Int): Char = "abc"[index]
    override fun subSequence(startIndex: Int, endIndex: Int): CharSequence {
        slices++
        return "abc".substring(startIndex, endIndex)
    }
    override fun toString(): String = "rendered"
}

private fun builderRangeFailure(expected: Int, block: () -> Unit) {
    val actual = try { block(); 0 }
    // Catch native ArgumentOutOfRangeException before the widened Kotlin bounds catch.
    catch (_: IllegalArgumentException) { 2 }
    catch (_: IndexOutOfBoundsException) { 1 }
    check(actual == expected) { "bounds kind expected=$expected actual=$actual" }
}

private fun reversedBuilderCharacters(input: CharArray): String {
    val result = CharArray(input.size)
    var read = input.lastIndex
    var write = 0
    while (read >= 0) {
        val character = input[read--]
        if (character.code in 0xdc00..0xdfff && read >= 0 && input[read].code in 0xd800..0xdbff) {
            result[write++] = input[read--]
        }
        result[write++] = character
    }
    return result.concatToString()
}

class PortableBuilderTests {
    @TestAttribute fun reversePreservesEveryShortSurrogateArrangement() {
        val alphabet = charArrayOf('A', 'B', 0xd800.toChar(), 0xdc00.toChar())
        var count = 1
        for (length in 0..7) {
            for (encoding in 0 until count) {
                var remaining = encoding
                val input = CharArray(length) {
                    val character = alphabet[remaining and 3]
                    remaining = remaining ushr 2
                    character
                }
                val builder = StringBuilder(input.concatToString())
                check(builder.reverse() === builder)
                check(builder.toString() == reversedBuilderCharacters(input)) { "length=$length encoding=$encoding" }
            }
            count *= 4
        }
        check(StringBuilder("A😀B").reverse().toString() == "B😀A")
    }

    @TestAttribute fun reverseRetainsNativeIdentityCapacityAndChunkedContents() {
        val builder = StringBuilder(128).append("A😀B")
        val native = builder as System.Text.StringBuilder
        val capacity = builder.capacity()
        check(builder.reverse() === builder)
        check((native as Any) === (builder as Any))
        check(native.ToString() == "B😀A" && builder.capacity() == capacity)
        native.Append("!")
        check(builder.toString() == "B😀A!")
        val text = "x😀y".repeat(4096)
        val chunked = StringBuilder()
        repeat(4096) { chunked.append("x😀y") }
        check(chunked.reverse() === chunked)
        check(chunked.toString() == reversedBuilderCharacters(text.toCharArray()))
        check(chunked.clear() === chunked && chunked.length == 0)
    }

    @TestAttribute fun deletionAndReplacementClampOnlyTheEndIndex() {
        for (start in 0..4) for (end in intArrayOf(start, 4, 5, Int.MAX_VALUE)) {
            val deleted = StringBuilder("abcd")
            check(deleted.deleteRange(start, end) === deleted)
            check(deleted.toString() == "abcd".substring(0, start) + "abcd".substring(end.coerceAtMost(4)))
            for (replacement in listOf("", "!", "longer")) {
                val replaced = StringBuilder("abcd")
                check(replaced.setRange(start, end, replacement) === replaced)
                check(replaced.toString() == "abcd".substring(0, start) + replacement + "abcd".substring(end.coerceAtMost(4)))
            }
        }
        for ((start, end) in listOf(-1 to 0, 3 to 2, 5 to 7, Int.MIN_VALUE to Int.MAX_VALUE, 0 to Int.MIN_VALUE)) {
            val builder = StringBuilder("abcd")
            val expected = if (start < 0 || start > 4) 1 else 2
            builderRangeFailure(expected) { builder.deleteRange(start, end) }
            check(builder.toString() == "abcd")
            builderRangeFailure(expected) { builder.setRange(start, end, "changed") }
            check(builder.toString() == "abcd")
        }
        for (index in 0..3) {
            val builder = StringBuilder("abcd")
            check(builder.deleteAt(index) === builder)
            check(builder.toString() == "abcd".removeRange(index, index + 1))
        }
        for (index in intArrayOf(-1, 4, Int.MAX_VALUE)) {
            val builder = StringBuilder("abcd")
            builderRangeFailure(1) { builder.deleteAt(index) }
            check(builder.toString() == "abcd")
        }
    }

    @TestAttribute fun arrayRangesValidateBeforeMutationAndKeepExclusiveEnds() {
        val source = charArrayOf('a', 'b', 'c', 'd')
        for (start in 0..4) for (end in start..4) {
            val expected = source.concatToString(start, end)
            val appended = StringBuilder("!")
            check(appended.appendRange(source, start, end) === appended)
            check(appended.toString() == "!$expected")
            val inserted = StringBuilder("[]")
            check(inserted.insertRange(1, source, start, end) === inserted)
            check(inserted.toString() == "[$expected]")
        }
        for ((start, end) in listOf(-1 to -1, 0 to 5, 3 to 2, Int.MIN_VALUE to Int.MAX_VALUE)) {
            val builder = StringBuilder("unchanged")
            val expected = if (start < 0 || end > source.size) 1 else 2
            builderRangeFailure(expected) { builder.appendRange(source, start, end) }
            builderRangeFailure(expected) { builder.insertRange(1, source, start, end) }
            check(builder.toString() == "unchanged")
        }
        val builder = StringBuilder("[]")
        builderRangeFailure(1) { builder.insertRange(-1, source, 0, 0) }
        builderRangeFailure(1) { builder.insertRange(3, source, 0, 0) }
        check(builder.toString() == "[]")
    }

    @TestAttribute fun characterCopiesValidateBothRangesAndKeepDestinationEdges() {
        val builder = StringBuilder("abcd")
        for (start in 0..4) for (end in start..4) {
            val destination = CharArray(end - start + 2) { '?' }
            builder.toCharArray(destination, 1, start, end)
            check(destination.concatToString() == "?" + "abcd".substring(start, end) + "?")
            check(builder.substring(start, end) == "abcd".substring(start, end))
            check(builder.subSequence(start, end).toString() == "abcd".substring(start, end))
        }
        val whole = CharArray(4)
        builder.toCharArray(whole)
        check(whole.concatToString() == "abcd")
        for ((start, end) in listOf(-1 to 0, 0 to 5, 3 to 2, Int.MIN_VALUE to Int.MAX_VALUE)) {
            val destination = CharArray(4) { '?' }
            val expected = if (start < 0 || end > builder.length) 1 else 2
            builderRangeFailure(expected) { builder.toCharArray(destination, 0, start, end) }
            builderRangeFailure(expected) { builder.substring(start, end) }
            builderRangeFailure(expected) { builder.subSequence(start, end) }
            check(destination.concatToString() == "????")
        }
        for (offset in intArrayOf(-1, 3, Int.MAX_VALUE)) {
            val destination = CharArray(4) { '?' }
            builderRangeFailure(if (offset == Int.MAX_VALUE) 2 else 1) { builder.toCharArray(destination, offset, 0, 2) }
            check(destination.concatToString() == "????")
        }
        check(builder.substring(4) == "" && builder.substring(1) == "bcd")
    }

    @TestAttribute fun nullableAndStringSequenceRangesKeepSnapshotAndEvaluationPolicy() {
        val absent: CharSequence? = null
        check(StringBuilder().append(absent, 1, 3).toString() == "ul")
        check(StringBuilder().append(null as Any?).toString() == "null")
        val custom = BuilderRangeSequence()
        check(StringBuilder().appendRange(custom, 1, 3).toString() == "bc")
        check(StringBuilder("!").insertRange(0, custom, 1, 3).toString() == "bc!")
        for ((start, end) in listOf(-1 to -1, 0 to 4, 2 to 1, Int.MIN_VALUE to Int.MAX_VALUE)) {
            val builder = StringBuilder("!")
            val before = custom.slices
            val expected = if (start < 0 || end > custom.length) 1 else 2
            builderRangeFailure(expected) { builder.appendRange(custom, start, end) }
            builderRangeFailure(expected) { builder.insertRange(0, custom, start, end) }
            check(custom.slices == before && builder.toString() == "!")
        }
        for (start in 0..4) for (end in start..4) {
            val source: CharSequence = "abcd"
            check(StringBuilder().append(source, start, end).toString() == "abcd".substring(start, end))
            check(StringBuilder("[]").insertRange(1, source, start, end).toString() == "[" + "abcd".substring(start, end) + "]")
        }
        var trace = ""
        fun receiver(): StringBuilder { trace += "r"; return StringBuilder() }
        fun value(): CharSequence? { trace += "v"; return null }
        fun start(): Int { trace += "s"; return 1 }
        fun end(): Int { trace += "e"; return 3 }
        check(receiver().append(value(), start(), end()).toString() == "ul")
        check(trace == "rvse")
    }

    @TestAttribute fun scalarForwardersUseTheExistingClrToStringContract() {
        val before = System.Globalization.CultureInfo.CurrentCulture
        try {
            for (culture in listOf(System.Globalization.CultureInfo.InvariantCulture, System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))) {
                System.Globalization.CultureInfo.CurrentCulture = culture
                for (value in listOf(true, false)) {
                    check(StringBuilder().append(value).toString() == value.toString())
                    check(StringBuilder("!").insert(0, value).toString() == value.toString() + "!")
                }
                for (value in byteArrayOf(Byte.MIN_VALUE, 0, Byte.MAX_VALUE)) {
                    check(StringBuilder().append(value).toString() == value.toString())
                    check(StringBuilder("!").insert(0, value).toString() == value.toString() + "!")
                }
                for (value in shortArrayOf(Short.MIN_VALUE, 0, Short.MAX_VALUE)) {
                    check(StringBuilder().append(value).toString() == value.toString())
                    check(StringBuilder("!").insert(0, value).toString() == value.toString() + "!")
                }
                for (value in intArrayOf(Int.MIN_VALUE, 0, Int.MAX_VALUE)) {
                    check(StringBuilder().append(value).toString() == value.toString())
                    check(StringBuilder("!").insert(0, value).toString() == value.toString() + "!")
                }
                for (value in longArrayOf(Long.MIN_VALUE, 0, Long.MAX_VALUE)) {
                    check(StringBuilder().append(value).toString() == value.toString())
                    check(StringBuilder("!").insert(0, value).toString() == value.toString() + "!")
                }
                for (value in floatArrayOf(-0.0f, 0.0f, 1.25f, Float.NaN, Float.POSITIVE_INFINITY, Float.MIN_VALUE, Float.MAX_VALUE)) {
                    check(StringBuilder().append(value).toString() == value.toString())
                    check(StringBuilder("!").insert(0, value).toString() == value.toString() + "!")
                }
                for (value in doubleArrayOf(-0.0, 0.0, 1.25, Double.NaN, Double.NEGATIVE_INFINITY, Double.MIN_VALUE, Double.MAX_VALUE)) {
                    check(StringBuilder().append(value).toString() == value.toString())
                    check(StringBuilder("!").insert(0, value).toString() == value.toString() + "!")
                }
            }
        } finally {
            System.Globalization.CultureInfo.CurrentCulture = before
        }
    }

    @TestAttribute fun savedCallablesAndLineWrappersKeepTheSameBuilder() {
        val reverse: (StringBuilder) -> StringBuilder = StringBuilder::reverse
        val append: (StringBuilder, Boolean) -> StringBuilder = StringBuilder::append
        val insert: (StringBuilder, Int, Double) -> StringBuilder = StringBuilder::insert
        val builder = StringBuilder("A😀B")
        check(reverse(builder) === builder && builder.toString() == "B😀A")
        check(append(builder, true) === builder)
        check(insert(builder, builder.length, 1.25) === builder)
        check(builder.toString() == "B😀A" + true.toString() + 1.25.toString())
        val line = StringBuilder().appendLine(1.toByte()).appendLine(2.toShort()).appendLine(3).appendLine(4L).appendLine(1.25f).appendLine(2.5)
        check(line.toString() == "1\n2\n3\n4\n" + 1.25f.toString() + "\n" + 2.5.toString() + "\n")
    }
}
