import NUnit.Framework.TestAttribute

private interface ConditionalJoinValue { fun value(): Int }
private class ConditionalJoinLeft : ConditionalJoinValue { override fun value() = 1 }
private class ConditionalJoinRight : ConditionalJoinValue { override fun value() = 2 }
private fun conditionalJoinChoose(flag: Boolean, left: ConditionalJoinLeft, right: ConditionalJoinRight): ConditionalJoinValue =
    if (flag) left else right
private fun conditionalJoinRead(value: ConditionalJoinValue): Int = value.value()
private fun conditionalJoinArgument(flag: Boolean): Int =
    conditionalJoinRead(if (flag) ConditionalJoinLeft() else ConditionalJoinRight())
private fun conditionalJoinWhen(choice: Int): ConditionalJoinValue = when (choice) {
    0 -> ConditionalJoinLeft()
    1 -> ConditionalJoinRight()
    else -> ConditionalJoinLeft()
}
private fun conditionalJoinElvis(left: ConditionalJoinLeft?): ConditionalJoinValue =
    left ?: ConditionalJoinRight()

private interface ConditionalJoinGeneric<T> { fun read(): T }
private interface ConditionalJoinDerived<T> : ConditionalJoinGeneric<T>
private class ConditionalJoinFirst<T>(private val value: T) : ConditionalJoinDerived<T> {
    override fun read(): T = value
}
private class ConditionalJoinSecond<T>(private val value: T) : ConditionalJoinDerived<T> {
    override fun read(): T = value
}
private fun <T> conditionalJoinGeneric(flag: Boolean, first: T, second: T): ConditionalJoinGeneric<T> =
    if (flag) ConditionalJoinFirst(first) else ConditionalJoinSecond(second)

private fun conditionalJoinNested(first: Boolean, second: Boolean, trace: StringBuilder): ConditionalJoinValue {
    val selected: ConditionalJoinValue = if (first) {
        trace.append("outer;")
        if (second) {
            trace.append("left;")
            ConditionalJoinLeft()
        } else {
            trace.append("right;")
            ConditionalJoinRight()
        }
    } else {
        trace.append("fallback;")
        ConditionalJoinRight()
    }
    return selected
}

private fun conditionalJoinNullable(flag: Boolean): ConditionalJoinValue? =
    if (flag) ConditionalJoinLeft() else null
private fun conditionalJoinThrow(flag: Boolean): ConditionalJoinValue =
    if (flag) ConditionalJoinLeft() else throw IllegalStateException("join")
private fun conditionalJoinNullableInt(flag: Boolean): Int? = if (flag) 3 else null
private fun conditionalJoinAny(flag: Boolean): Any = if (flag) 7 else ConditionalJoinRight()
private fun conditionalJoinUnit(flag: Boolean, trace: StringBuilder): Unit {
    val result: Unit = if (flag) {
        trace.append("left;")
        Unit
    } else {
        trace.append("right;")
        Unit
    }
    return result
}

class ConditionalJoinTests {
    @TestAttribute
    fun interfaceReturnsAndArgumentsRetainTheSelectedObject() {
        val left = ConditionalJoinLeft()
        val right = ConditionalJoinRight()
        check(conditionalJoinChoose(true, left, right) === left)
        check(conditionalJoinChoose(false, left, right) === right)
        check(conditionalJoinArgument(true) == 1)
        check(conditionalJoinArgument(false) == 2)
        check(conditionalJoinWhen(0).value() == 1)
        check(conditionalJoinWhen(1).value() == 2)
        check(conditionalJoinWhen(2).value() == 1)
        check(conditionalJoinElvis(left) === left)
        check(conditionalJoinElvis(null).value() == 2)
    }

    @TestAttribute
    fun inheritedGenericInterfaceResultsKeepTheirFrame() {
        check(conditionalJoinGeneric(true, 11, 12).read() == 11)
        check(conditionalJoinGeneric(false, "a", "b").read() == "b")
        check(conditionalJoinGeneric<Int?>(false, 13, null).read() == null)
    }

    @TestAttribute
    fun nestedJoinsOnlyEvaluateSelectedBranches() {
        val trace = StringBuilder()
        check(conditionalJoinNested(true, true, trace).value() == 1)
        check(conditionalJoinNested(true, false, trace).value() == 2)
        check(conditionalJoinNested(false, true, trace).value() == 2)
        check(trace.toString() == "outer;left;outer;right;fallback;")
    }

    @TestAttribute
    fun nullThrowAndBoxedValueBranchesRemainValid() {
        check(conditionalJoinNullable(true)?.value() == 1)
        check(conditionalJoinNullable(false) == null)
        check(conditionalJoinThrow(true).value() == 1)
        var caught = false
        try {
            conditionalJoinThrow(false)
        } catch (failure: IllegalStateException) {
            check(failure.message == "join")
            caught = true
        }
        check(caught)
        check(conditionalJoinNullableInt(true) == 3)
        check(conditionalJoinNullableInt(false) == null)
        check(conditionalJoinAny(true) == 7)
        check((conditionalJoinAny(false) as ConditionalJoinValue).value() == 2)
        val trace = StringBuilder()
        val first: Any = conditionalJoinUnit(true, trace)
        val second: Any = conditionalJoinUnit(false, trace)
        check(first === Unit && second === Unit)
        check(trace.toString() == "left;right;")
    }
}
