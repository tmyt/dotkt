import NUnit.Framework.TestAttribute
import System.TimeSpan

private fun boxedConditionalInt(flag: Boolean): Any = if (flag) 7 else "text"
private fun boxedConditionalBoolean(flag: Boolean): Any = if (flag) true else "text"
private fun boxedConditionalChar(flag: Boolean): Any = if (flag) 'x' else "text"
private fun boxedConditionalDouble(flag: Boolean): Any = if (flag) 1.5 else "text"
private fun boxedConditionalNullable(flag: Boolean, value: Int?): Any? = if (flag) value else "text"
private fun <T : Comparable<T>> boxedConditionalGeneric(flag: Boolean, value: T): Comparable<*> =
    if (flag) value else "text"
private fun boxedConditionalArgument(value: Comparable<*>): Any = value
private fun boxedConditionalStruct(flag: Boolean, value: TimeSpan): Any = if (flag) value else "text"

private fun boxedConditionalNested(first: Boolean, second: Boolean, trace: StringBuilder): Any {
    val selected = if (first) {
        trace.append("outer;")
        if (second) { trace.append("int;"); 7 } else { trace.append("text;"); "text" }
    } else { trace.append("char;"); 'x' }
    return boxedConditionalArgument(selected)
}

class BoxedConditionalTests {
    @TestAttribute
    fun primitiveAndNullableBranchesBoxBeforeTheirReferenceJoin() {
        check(boxedConditionalInt(true) == 7)
        check(boxedConditionalInt(false) == "text")
        check(boxedConditionalBoolean(true) == true)
        check(boxedConditionalBoolean(false) == "text")
        check(boxedConditionalChar(true) == 'x')
        check(boxedConditionalChar(false) == "text")
        check(boxedConditionalDouble(true) == 1.5)
        check(boxedConditionalDouble(false) == "text")
        check(boxedConditionalNullable(true, 7) == 7)
        check(boxedConditionalNullable(true, null) == null)
        check(boxedConditionalNullable(false, null) == "text")
    }

    @TestAttribute
    fun genericAndStructValuesRetainTheirValueAcrossReferenceSlots() {
        check(boxedConditionalGeneric(true, 7) == 7)
        check(boxedConditionalGeneric(false, 7) == "text")
        check(boxedConditionalGeneric(true, "value") == "value")
        check(boxedConditionalGeneric(false, "value") == "text")
        val span = TimeSpan(0, 0, 5)
        check((boxedConditionalStruct(true, span) as TimeSpan).CompareTo(span) == 0)
        check(boxedConditionalStruct(false, span) == "text")
        check(boxedConditionalArgument(if (true) 7 else "text") == 7)
        check(boxedConditionalArgument(if (false) 7 else "text") == "text")
    }

    @TestAttribute
    fun nestedReferenceJoinsEvaluateOnlyTheirSelectedBranch() {
        val trace = StringBuilder()
        check(boxedConditionalNested(true, true, trace) == 7)
        check(boxedConditionalNested(true, false, trace) == "text")
        check(boxedConditionalNested(false, true, trace) == 'x')
        check(trace.toString() == "outer;int;outer;text;char;")
    }
}
