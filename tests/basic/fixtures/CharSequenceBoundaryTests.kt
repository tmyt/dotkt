import NUnit.Framework.TestAttribute
import kotlin.time.Duration.Companion.milliseconds

private class BoundaryText(private val text: String) : CharSequence {
    override val length: Int get() = text.length
    override fun get(index: Int): Char = text[index]
    override fun subSequence(startIndex: Int, endIndex: Int): CharSequence =
        BoundaryText(text.substring(startIndex, endIndex))
    override fun toString(): String = "rendered"
}

private inline fun CharSequence.boundaryIndex(predicate: (Char) -> Boolean): Int {
    for (index in 0 until length) if (predicate(get(index))) return index
    return -1
}

private fun boundaryObject(flag: Boolean, other: CharSequence): Any = if (flag) "raw" else other
private fun boundaryRendered(flag: Boolean, other: CharSequence): String = (if (flag) "raw" else other).toString()
private fun boundaryElvis(other: CharSequence?): String = (other ?: "raw").toString()
private fun boundaryNullable(flag: Boolean, value: String?, other: CharSequence): Any? =
    if (flag) value else other
private fun boundaryAbsent(): CharSequence? = null

class CharSequenceBoundaryTests {
    @TestAttribute
    fun inlineReceiversAdaptStringsWithoutSnapshottingCustomSequences() {
        var evaluations = 0
        var predicates = 0
        fun stringOnce(): String { evaluations++; return "abc" }
        check(stringOnce().boundaryIndex { predicates++; it == 'b' } == 1)
        check(evaluations == 1 && predicates == 2)
        val custom = BoundaryText("abc")
        check(custom.boundaryIndex { it == 'b' } == 1)
        check(custom.length == 3 && custom.toString() == "rendered")
    }

    @TestAttribute
    fun conditionalAndElvisJoinsHonorTheirOwnCharSequenceSlot() {
        val custom = BoundaryText("abc")
        var evaluations = 0
        fun customOnce(): CharSequence { evaluations++; return custom }
        check(boundaryObject(true, customOnce()).toString() == "raw")
        check(boundaryObject(false, customOnce()) === custom)
        check(evaluations == 2)
        check(boundaryRendered(true, custom) == "raw")
        check(boundaryRendered(false, custom) == "rendered")
        check(boundaryElvis(null) == "raw" && boundaryElvis(custom) == "rendered")
        check(boundaryNullable(true, null, custom) == null)
        check(boundaryNullable(true, "text", custom).toString() == "text")
        check(boundaryNullable(false, null, custom) === custom)
    }

    @TestAttribute
    fun indentationFilterAndFractionalDurationExecuteActualStdlibBodies() {
        check("  a\n    b".trimIndent() == "a\n  b")
        check("  |a\n  |b".trimMargin() == "a\nb")
        check("  |a\n  |b".replaceIndentByMargin("--") == "--a\n--b")
        val filter: (String, (Char) -> Boolean) -> String = String::filter
        check(filter("a1b2") { it in 'a'..'z' } == "ab")
        check(1250.milliseconds.toString() == "1.25s")
        check(1234.milliseconds.toString() == "1.234s")
    }

    @TestAttribute
    fun nullableStringBuilderBodiesPreserveNullAndCustomRendering() {
        val absent = boundaryAbsent()
        val custom: CharSequence = BoundaryText("abc")
        check(StringBuilder().append(absent).toString() == "null")
        check(StringBuilder("X").insert(0, absent).toString() == "nullX")
        check(StringBuilder().append(custom).toString() == "rendered")
        check(StringBuilder("X").insert(0, custom).toString() == "renderedX")
        check(StringBuilder().append("text" as CharSequence?).toString() == "text")
    }
}
