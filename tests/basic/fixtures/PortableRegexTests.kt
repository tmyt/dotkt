import NUnit.Framework.TestAttribute

private class RegexObservedInput(private val text: String) : CharSequence {
    var snapshots: Int = 0
    override val length: Int get() = text.length
    override fun get(index: Int): Char = text[index]
    override fun subSequence(startIndex: Int, endIndex: Int): CharSequence = text.substring(startIndex, endIndex)
    override fun toString(): String {
        snapshots++
        return text
    }
}

private fun expectRegexExhaustion(action: () -> Unit) {
    var rejected = false
    try { action() } catch (e: NoSuchElementException) { rejected = true }
    check(rejected)
}

private fun expectRegexBounds(action: () -> Unit) {
    var rejected = false
    try { action() } catch (e: IndexOutOfBoundsException) { rejected = true }
    check(rejected)
}

private fun expectRegexLimitFailure(action: () -> Unit) {
    var rejected = false
    try { action() } catch (e: IllegalArgumentException) { rejected = true }
    check(rejected)
}

class PortableRegexTests {
    @TestAttribute fun splitSequenceDefersInputWorkAndCanBeReiterated() {
        val input = RegexObservedInput("a,b,")
        val parts = Regex(",").splitToSequence(input)
        check(input.snapshots == 0)
        val iterator = parts.iterator()
        check(input.snapshots == 0)
        check(iterator.hasNext())
        check(input.snapshots == 1)
        check(iterator.next() == "a")
        check(iterator.next() == "b")
        check(iterator.next() == "")
        check(!iterator.hasNext())
        expectRegexExhaustion { iterator.next() }
        check(parts.toList() == listOf("a", "b", ""))
        check(input.snapshots == 2)
    }

    @TestAttribute fun splitWalkingRetainsLimitsEmptyPartsAndZeroWidthProgress() {
        val delimiter = Regex(",")
        check(delimiter.split(",a,,b,") == listOf("", "a", "", "b", ""))
        check(delimiter.split(",a,,b,", 1) == listOf(",a,,b,"))
        check(delimiter.split(",a,,b,", 2) == listOf("", "a,,b,"))
        check(delimiter.split(",a,,b,", 3) == listOf("", "a", ",b,"))
        for (input in listOf("", "a", ",a,,b,", "ab")) {
            for (limit in intArrayOf(0, 1, 2, 3, Int.MAX_VALUE)) {
                check(delimiter.splitToSequence(input, limit).toList() == delimiter.split(input, limit))
            }
        }
        val empty = Regex("")
        check(empty.split("ab") == listOf("", "a", "b", ""))
        check(empty.splitToSequence("ab").toList() == listOf("", "a", "b", ""))
        check(empty.splitToSequence("ab", 2).toList() == listOf("", "ab"))
        check(empty.splitToSequence("").toList() == listOf("", ""))
        check(Regex("z").splitToSequence("").toList() == listOf(""))
        val input = RegexObservedInput("a,b")
        expectRegexLimitFailure { delimiter.splitToSequence(input, -1) }
        expectRegexLimitFailure { delimiter.split(input, -1) }
        check(input.snapshots == 0)
    }

    @TestAttribute fun findAllChecksLengthWithoutPrematureMaterialization() {
        val input = RegexObservedInput("a1b22")
        val pattern = Regex("\\d+")
        val matches = pattern.findAll(input, 2)
        check(input.snapshots == 0)
        val iterator = matches.iterator()
        check(input.snapshots == 0)
        check(iterator.next().value == "22")
        check(!iterator.hasNext())
        check(input.snapshots == 1)
        for (index in intArrayOf(-1, input.length + 1)) {
            expectRegexBounds { pattern.findAll(input, index) }
        }
        check(input.snapshots == 1)
        check(pattern.findAll(input, input.length).none())
    }

    @TestAttribute fun groupsUseInheritedCollectionOperationsAndCheckedIteration() {
        val match = Regex("(?<word>a)(?<tail>b)?").find("a")!!
        val groups: MatchGroupCollection = match.groups
        check(groups.size == 3 && !groups.isEmpty())
        check(groups[0] == MatchGroup("a", 0..0))
        check(groups[1] == MatchGroup("a", 0..0) && groups[2] == null)
        check(groups[0] in groups && null in groups)
        check(groups.containsAll(listOf(groups[0], null)))
        check(MatchGroup("z", 0..0) !in groups)
        check(groups["word"]?.value == "a" && groups["tail"] == null)
        val iterator = groups.iterator()
        check(iterator.next() == groups[0])
        check(iterator.next() == groups[1])
        check(iterator.next() == null && !iterator.hasNext())
        expectRegexExhaustion { iterator.next() }
        expectRegexExhaustion { iterator.next() }
    }

    @TestAttribute fun groupValuesKeepListBoundsAndStableMatchContents() {
        val first = Regex("(?<word>a)(?<tail>b)?").find("a ab")!!
        val values = first.groupValues
        check(values == listOf("a", "a", ""))
        check(values.subList(1, values.size) == listOf("a", ""))
        check(values.indexOf("") == 2)
        for (index in intArrayOf(-1, values.size, Int.MAX_VALUE)) {
            expectRegexBounds { values[index] }
        }
        check(first.next()!!.groupValues == listOf("ab", "a", "b"))
        check(values == listOf("a", "a", ""))
        check(first.groupValues == values)
    }

    @TestAttribute fun commonDestructuredDefaultWorksThroughTheInterface() {
        val match: MatchResult = Regex("(a)(b)?").find("a")!!
        val (first, second) = match.destructured
        check(first == "a" && second == "")
        val destructure: (MatchResult) -> MatchResult.Destructured = { it.destructured }
        check(destructure(match).toList() == listOf("a", ""))
        check(Regex("(a)(b)").find("ab")!!.destructured.toList() == listOf("a", "b"))
    }
}
