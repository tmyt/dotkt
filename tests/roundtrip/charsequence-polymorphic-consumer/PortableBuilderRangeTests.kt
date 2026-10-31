package roundtriptests.charsequencepolymorphic

import NUnit.Framework.TestAttribute

private fun builderBoundsKind(action: () -> Unit): Int {
    try { action() }
    catch (_: IllegalArgumentException) { return 2 }
    catch (_: IndexOutOfBoundsException) { return 1 }
    return 0
}

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

class PortableBuilderRangeTests {
    @TestAttribute fun importedBuilderRangesKeepLogicalCharactersAndGuardBeforeCallbacks() {
        val custom = BuilderRangeSequence()
        check(StringBuilder().appendRange(custom, 1, 3).toString() == "bc")
        check(StringBuilder("!").insertRange(0, custom, 1, 3).toString() == "bc!")
        for ((start, end) in listOf(-1 to -1, 0 to 4, 2 to 1, Int.MIN_VALUE to Int.MAX_VALUE)) {
            val builder = StringBuilder("!")
            val before = custom.slices
            val expected = if (start < 0 || end > custom.length) 1 else 2
            check(builderBoundsKind { builder.appendRange(custom, start, end) } == expected)
            check(builderBoundsKind { builder.insertRange(0, custom, start, end) } == expected)
            check(custom.slices == before && builder.toString() == "!")
        }
    }
}
