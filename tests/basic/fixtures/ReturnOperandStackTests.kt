import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.clr.ClrRef
import kotlin.clr.byref

class ReturnOperandStackTests {
    private var trace = 0
    private fun mark(value: Int): Int { trace = trace * 10 + value; return value }

    private fun ordered(flag: Boolean): Int {
        return mark(1) + (if (flag) return mark(7) else mark(3))
    }

    private fun mutableLeft(): Int {
        var value = 1
        return value + run { value = 2; if (value == 3) return 9; value }
    }

    private fun arrayReturn(flag: Boolean): Int {
        val values = arrayOf(mark(1), if (flag) return mark(7) else mark(3))
        return values[0] + values[1]
    }

    private fun lazyReturn(flag: Boolean): Int {
        val result = flag && (if (mark(2) == 2) return 9 else false)
        return if (result) 1 else 0
    }

    private fun loopReturn(): Int {
        var count = 0
        while (1 + (if (count++ == 0) 0 else return 9) > 0) { }
        return 0
    }

    private fun protectedReturn(flag: Boolean): Int {
        try {
            return mark(1) + (if (flag) return mark(7) else mark(3))
        } finally {
            mark(9)
        }
    }

    private fun abruptOperand(): Int {
        return mark(1) + run<Int> { return mark(7) }
    }

    private fun nestedResult(flag: Boolean): Int {
        return run {
            val first = mark(1)
            first + mark(2) + (if (flag) return 7 else 3)
        }
    }

    private fun write(slot: ClrRef<Int>, value: Int) { slot.value = value }

    private fun nativeArgument(flag: Boolean): Int {
        var value = 0
        write(byref(value), if (flag) return 7 else 3)
        return value
    }

    private fun nativeStore(slot: ClrRef<Int>, flag: Boolean): Int {
        slot.value = if (flag) return 7 else 3
        return 0
    }

    private fun arrayLocation(flag: Boolean): Int {
        val original = intArrayOf(0, 0)
        var values = original
        var index = 0
        write(byref(values[index]), run {
            values = intArrayOf(8, 8)
            index = 1
            if (flag) return 7
            3
        })
        assertEquals(3, original[0])
        assertEquals(0, original[1])
        assertEquals(8, values[index])
        return 0
    }

    @TestAttribute
    fun blockResultsRemainAfterStatementsAndWithinLocalScope() {
        trace = 0; assertEquals(7, nestedResult(true)); assertEquals(12, trace)
        trace = 0; assertEquals(6, nestedResult(false)); assertEquals(12, trace)
    }

    @TestAttribute
    fun nativeReferenceArgumentsAndStoresKeepTheOriginalLocation() {
        assertEquals(7, nativeArgument(true))
        assertEquals(3, nativeArgument(false))
        var value = 0
        assertEquals(7, nativeStore(byref(value), true)); assertEquals(0, value)
        assertEquals(0, nativeStore(byref(value), false)); assertEquals(3, value)
        assertEquals(7, arrayLocation(true))
        assertEquals(0, arrayLocation(false))
    }

    @TestAttribute
    fun earlierOperandsRetainTheirEvaluationOrderAndValue() {
        trace = 0; assertEquals(7, ordered(true)); assertEquals(17, trace)
        trace = 0; assertEquals(4, ordered(false)); assertEquals(13, trace)
        assertEquals(3, mutableLeft())
    }

    @TestAttribute
    fun arrayOperandsCanReturnWithoutLeavingPendingValues() {
        trace = 0; assertEquals(7, arrayReturn(true)); assertEquals(17, trace)
        trace = 0; assertEquals(4, arrayReturn(false)); assertEquals(13, trace)
    }

    @TestAttribute
    fun shortCircuitAndLoopGuardsKeepReturnsConditional() {
        trace = 0; assertEquals(0, lazyReturn(false)); assertEquals(0, trace)
        assertEquals(9, lazyReturn(true)); assertEquals(2, trace)
        assertEquals(9, loopReturn())
    }

    @TestAttribute
    fun protectedAndAlwaysReturningOperandsPreserveEffects() {
        trace = 0; assertEquals(7, protectedReturn(true)); assertEquals(179, trace)
        trace = 0; assertEquals(4, protectedReturn(false)); assertEquals(139, trace)
        trace = 0; assertEquals(7, abruptOperand()); assertEquals(17, trace)
    }
}
