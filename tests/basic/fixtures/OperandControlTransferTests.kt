import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.clr.byref
import kotlin.clr.ClrRef
import kotlin.clr.ClrEnum

private fun consume(first: Int, second: Any) {}
private fun example(flag: Boolean, stop: Boolean): Int {
    consume(1, if (flag) {
        if (stop) return 9
        Unit
    } else Unit)
    return 0
}
private fun operandDirectReturn() { consume(operandMark("a", 1), return) }
private fun operandDirectUnitValueReturn() { consume(operandMark("a", 1), return Unit) }

private var operandTrace = ""
private fun operandMark(tag: String, value: Int): Int { operandTrace += tag; return value }
private fun operandPair(first: Int, second: Int): Int { operandTrace += "c"; return first * 10 + second }
private class OperandReceiver {
    fun call(first: Int, second: Int): Int = operandPair(first, second)
}
private fun operandReceiver(): OperandReceiver { operandTrace += "r"; return OperandReceiver() }
private interface OperandAction { fun apply(value: Int): Int }
private class OperandActionValue : OperandAction { override fun apply(value: Int): Int = value + 1 }
private fun <T : OperandAction> operandGenericReceiver(receiver: T, stop: Boolean): Int {
    return receiver.apply(if (stop) return 9 else operandMark("b", 2))
}
private suspend fun operandSuspendArgument(): Int = 2
private suspend fun <T : OperandAction> operandSuspendReceiver(receiver: T, stop: Boolean): Int {
    return receiver.apply(if (stop) return 9 else operandSuspendArgument())
}
private fun operandCall(flag: Boolean, stop: Boolean): Int { return operandPair(
    operandMark("a", 1),
    if (flag) { operandMark("b", 0); if (stop) return 9; operandMark("d", 2) }
    else operandMark("e", 3),
) }
private fun operandMember(stop: Boolean): Int { return operandReceiver().call(
    operandMark("a", 1), if (stop) return 9 else operandMark("b", 2),
) }
private fun operandNestedReceiver(stop: Boolean): Int { return operandPair(
    operandMark("a", 1),
    (if (stop) return 9 else operandReceiver()).call(operandMark("b", 1), operandMark("d", 2)),
) }
private fun operandInlineBlock(stop: Boolean): Int { return operandPair(operandMark("a", 1), run {
    operandMark("b", 0)
    operandPair(operandMark("d", 1), if (stop) return 9 else operandMark("e", 2))
}) }
private fun operandArithmetic(stop: Boolean): Int { return operandMark("a", 4) +
    if (stop) return 9 else operandMark("b", 5) }
private fun operandArray(stop: Boolean): Int {
    val array = intArrayOf(0)
    fun target(): IntArray { operandTrace += "a"; return array }
    target()[operandMark("i", 0)] = if (stop) return 9 else operandMark("v", 7)
    return array[0]
}
private fun operandConstruction(stop: Boolean): Int {
    val array = intArrayOf(operandMark("a", 1), if (stop) return 9 else operandMark("b", 2), operandMark("d", 3))
    return array.sum()
}
private fun operandConcat(stop: Boolean): String { return "prefix" + if (stop) return "early" else operandMark("b", 2) }
private fun operandCondition(stop: Boolean): Int {
    if (operandMark("a", 1) + (if (stop) return 9 else operandMark("b", 2)) == 3) return 3
    return 0
}
private fun operandLoop(): Int {
    var i = 0
    var sum = 0
    while (i < 4) {
        i++
        sum += operandPair(operandMark("a", i), if (i == 1) continue else if (i == 3) break else operandMark("b", i))
    }
    return sum
}
private fun operandRefCall(value: ClrRef<Int>, add: Int): Int { value.value += add; return value.value }
private fun operandRef(stop: Boolean): Int {
    val array = intArrayOf(1)
    val result = operandRefCall(byref(array[operandMark("i", 0)]), if (stop) return 9 else operandMark("v", 2))
    return result * 10 + array[0]
}
private fun operandRefSnapshot(stop: Boolean): Int {
    var index = 0
    var array = intArrayOf(1, 4)
    val original = array
    val other = intArrayOf(7, 8)
    val result = operandRefCall(byref(array[index]), if (stop) return 9 else {
        index = 1
        array = other
        2
    })
    return result * 100 + original[0] * 10 + other[0]
}
private fun operandRefBadIndex(stop: Boolean): Int {
    val array = intArrayOf(1)
    return operandRefCall(byref(array[operandMark("i", 2)]), if (stop) return 9 else operandMark("v", 2))
}
@ClrEnum
private enum class OperandNativeEnum(value: Int) { A(0), B(1) }
private fun operandEnum(stop: Boolean): Int {
    val value = enumValueOf<OperandNativeEnum>(if (stop) return 9 else "B")
    return value.ordinal
}
private enum class OperandPlainEnum { A, B }
private fun operandPlainEnum(stop: Boolean): Int {
    val value = enumValueOf<OperandPlainEnum>(if (stop) return 9 else "B")
    return value.ordinal
}
private fun operandInitializer(first: Int, init: (Int) -> Int): (Int) -> Int = init
private fun operandArrayInitializer(stop: Boolean): Int {
    val array = IntArray(operandMark("s", 2), operandInitializer(
        operandMark("f", 0), if (stop) return 9 else { index: Int -> index + 1 },
    ))
    return array.sum()
}

class OperandControlTransferTests {
    @TestAttribute fun originalUnitArgumentHasValidTakenAndUntakenReturns() {
        assertEquals(9, example(true, true))
        assertEquals(0, example(true, false))
        assertEquals(0, example(false, true))
        assertEquals(0, example(false, false))
        operandTrace = ""; operandDirectReturn(); assertEquals("a", operandTrace)
        operandTrace = ""; operandDirectUnitValueReturn(); assertEquals("a", operandTrace)
    }
    @TestAttribute fun orderedArgumentsRunOnceAndOnlyTheSelectedBranchRuns() {
        operandTrace = ""; assertEquals(9, operandCall(true, true)); assertEquals("ab", operandTrace)
        operandTrace = ""; assertEquals(12, operandCall(true, false)); assertEquals("abdc", operandTrace)
        operandTrace = ""; assertEquals(13, operandCall(false, true)); assertEquals("aec", operandTrace)
    }
    @TestAttribute fun receiverAndArgumentsStayBeforeTheReturnOperand() {
        operandTrace = ""; assertEquals(9, operandMember(true)); assertEquals("ra", operandTrace)
        operandTrace = ""; assertEquals(12, operandMember(false)); assertEquals("rabc", operandTrace)
        operandTrace = ""; assertEquals(9, operandNestedReceiver(true)); assertEquals("a", operandTrace)
        operandTrace = ""; assertEquals(22, operandNestedReceiver(false)); assertEquals("arbdcc", operandTrace)
        operandTrace = ""; assertEquals(9, operandInlineBlock(true)); assertEquals("abd", operandTrace)
        operandTrace = ""; assertEquals(22, operandInlineBlock(false)); assertEquals("abdecc", operandTrace)
        operandTrace = ""; assertEquals(9, operandGenericReceiver(OperandActionValue(), true)); assertEquals("", operandTrace)
        operandTrace = ""; assertEquals(3, operandGenericReceiver(OperandActionValue(), false)); assertEquals("b", operandTrace)
    }
    @TestAttribute fun arithmeticAndArrayStoreOperandsKeepTheirOrder() {
        operandTrace = ""; assertEquals(9, operandArithmetic(true)); assertEquals("a", operandTrace)
        operandTrace = ""; assertEquals(9, operandArithmetic(false)); assertEquals("ab", operandTrace)
        operandTrace = ""; assertEquals(9, operandArray(true)); assertEquals("ai", operandTrace)
        operandTrace = ""; assertEquals(7, operandArray(false)); assertEquals("aiv", operandTrace)
    }
    @TestAttribute fun constructionAndConcatenationDoNotLeaveTheirAccumulatorsLive() {
        operandTrace = ""; assertEquals(9, operandConstruction(true)); assertEquals("a", operandTrace)
        operandTrace = ""; assertEquals(6, operandConstruction(false)); assertEquals("abd", operandTrace)
        operandTrace = ""; assertEquals("early", operandConcat(true)); assertEquals("", operandTrace)
        operandTrace = ""; assertEquals("prefix2", operandConcat(false)); assertEquals("b", operandTrace)
    }
    @TestAttribute fun normalizationStaysInsideConditionAndLoopEvaluationSites() {
        operandTrace = ""; assertEquals(9, operandCondition(true)); assertEquals("a", operandTrace)
        operandTrace = ""; assertEquals(3, operandCondition(false)); assertEquals("ab", operandTrace)
        operandTrace = ""; assertEquals(22, operandLoop()); assertEquals("aabca", operandTrace)
        var i = 0
        while (operandPair(operandMark("x", 0), if (++i < 3) 1 else 0) == 1) {}
        assertEquals(3, i)
        assertEquals("aabcaxcxcxc", operandTrace)
    }
    @TestAttribute fun earlierLocalReadsAreSnapshotsBeforeLaterOperandWrites() {
        var value = 1
        assertEquals(12, operandPair(value, if (true) { value = 8; if (value < 0) return; 2 } else 3))
        assertEquals(8, value)
    }
    @TestAttribute fun deferredLambdaBodyIsNotAnOperandTransfer() {
        operandTrace = ""
        val callback = callback@{ operandMark("b", 2); if (true) return@callback 9 else 0 }
        operandMark("a", 1)
        assertEquals("a", operandTrace)
        assertEquals(9, callback())
        assertEquals("ab", operandTrace)
    }
    @TestAttribute fun earlierManagedReferenceOperandsPreserveTheOriginalStorage() {
        operandTrace = ""; assertEquals(9, operandRef(true)); assertEquals("i", operandTrace)
        operandTrace = ""; assertEquals(33, operandRef(false)); assertEquals("iv", operandTrace)
        assertEquals(9, operandRefSnapshot(true))
        assertEquals(337, operandRefSnapshot(false))
        operandTrace = ""
        val caught = try { operandRefBadIndex(true); false } catch (error: IndexOutOfBoundsException) { true }
        assertEquals(true, caught)
        assertEquals("i", operandTrace)
    }
    @TestAttribute fun implicitEmitterOperandsAndSeparatelyStoredConstructorOperandsAreOrdered() {
        assertEquals(9, operandEnum(true))
        assertEquals(1, operandEnum(false))
        assertEquals(9, operandPlainEnum(true))
        assertEquals(1, operandPlainEnum(false))
        operandTrace = ""; assertEquals(9, operandArrayInitializer(true)); assertEquals("sf", operandTrace)
        operandTrace = ""; assertEquals(3, operandArrayInitializer(false)); assertEquals("sf", operandTrace)
    }
}
