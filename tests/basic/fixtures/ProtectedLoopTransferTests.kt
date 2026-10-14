import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue

private open class ProtectedLoopBase(val total: Int)
private class ProtectedLoopDerived : ProtectedLoopBase(run {
    var total = 0
    for (index in 0..3) total += index
    total
})
private class ProtectedLoopDelegating(val total: Int) {
    constructor() : this(run {
        var total = 0
        for (index in 0..3) {
            try {
                if (index == 0) continue
                if (index == 2) break
                total += index
            } finally { total += 10 }
        }
        total
    })
}

// Physical branch ownership: a local loop jump must stay in its handler; an outward one must unwind precisely
// the regions it exits. Cleanup itself may replace a pending loop jump, return, or exception.
class ProtectedLoopTransferTests {
    @TestAttribute
    fun constructorArgumentLoopsHaveTheirOwnPhysicalTargets() {
        assertEquals(6, ProtectedLoopDerived().total)
        assertEquals(31, ProtectedLoopDelegating().total)
    }

    @TestAttribute
    fun expressionReturnCanReplaceAndBeReplacedByLoopCompletion() {
        fun replaceBreak(): Int {
            for (index in 0..1) {
                try { break } finally {
                    val ignored = if (index == 0) return 17 else 0
                }
            }
            return 19
        }
        var trace = ""
        fun replaceReturn(stop: Boolean): Int {
            for (index in 0..1) {
                try {
                    val ignored = if (index == 0) return 7 else 0
                } finally {
                    trace += "f"
                    if (stop) break
                    if (index == 0) continue
                }
            }
            return 9
        }
        assertEquals(17, replaceBreak())
        assertEquals(9, replaceReturn(false))
        assertEquals(9, replaceReturn(true))
        assertEquals("fff", trace)
    }

    @TestAttribute
    fun originalWitness() {
        var trace = ""
        for (index in 0..3) {
            try {
                if (index == 0) continue
                if (index == 2) break
                trace += "b"
            } finally { trace += "f" }
        }
        check(trace == "fbff")
    }

    @TestAttribute
    fun catchExitsAndUnitTryConsumer() {
        var trace = ""
        for (index in 0..3) {
            val value: Any = try {
                throw IllegalStateException("caught")
            } catch (failure: IllegalStateException) {
                if (index == 0) continue
                if (index == 2) break
                trace += "c"
                Unit
            } finally { trace += "f" }
            assertTrue(value === Unit)
            trace += "v"
        }
        assertEquals("fcfvf", trace)
        trace = ""
        for (index in 0..3) {
            val value: Any = try {
                if (index == 0) continue
                if (index == 2) break
                trace += "b"
                Unit
            } finally { trace += "f" }
            assertTrue(value === Unit)
            trace += "v"
        }
        assertEquals("fbfvf", trace)
    }

    @TestAttribute
    fun nestedLabeledExitsUnwindOnlyCrossedRegions() {
        var trace = ""
        outer@ for (index in 0..3) {
            try {
                for (inner in intArrayOf(0, 1, 2)) {
                    try {
                        if (inner == 0) continue
                        if (index == 0) continue@outer
                        if (index == 2) break@outer
                        trace += "b"
                        break
                    } finally { trace += "i" }
                }
                trace += "a"
            } finally { trace += "o" }
        }
        assertEquals("iioibiaoiio", trace)
    }

    @TestAttribute
    fun loopsEntirelyInsideTryCatchAndFinally() {
        var trace = ""
        try {
            for (index in 0..3) {
                if (index == 0) continue
                if (index == 2) break
                trace += "t"
            }
            throw IllegalStateException("catch")
        } catch (failure: IllegalStateException) {
            var index = 0
            while (index < 4) {
                index++
                if (index == 1) continue
                if (index == 3) break
                trace += "c"
            }
        } finally {
            var index = 0
            do {
                index++
                try {
                    if (index == 1) continue
                    if (index == 3) break
                    trace += "f"
                } finally { trace += "i" }
            } while (index < 4)
            trace += "e"
        }
        assertEquals("tcifiie", trace)
    }

    @TestAttribute
    fun finallyOverridesPendingBreakAndException() {
        var trace = ""
        for (index in 0..3) {
            try {
                trace += "t"
                if (index == 0) break
                throw IllegalStateException("suppressed")
            } finally {
                trace += "f"
                if (index < 2) continue
                break
            }
        }
        assertEquals("tftftf", trace)
    }

    @TestAttribute
    fun escapingFinallyFallsThroughAndPreservesExceptions() {
        var trace = ""
        val failure = IllegalStateException("original")
        var observed: Throwable? = null
        try {
            for (index in 0..2) {
                try {
                    trace += "t"
                    if (index == 2) throw failure
                } finally {
                    trace += "f"
                    if (index == 0) continue
                    if (index == 3) break
                }
                trace += "v"
            }
        } catch (caught: IllegalStateException) { observed = caught }
        assertTrue(observed === failure)
        assertEquals("tftfvtf", trace)
    }

    @TestAttribute
    fun cleanupFailureBypassesSourceCatchAndInlineLoopExit() {
        var trace = ""
        val failure = IllegalStateException("cleanup")
        var observed: Throwable? = null
        try {
            for (index in 0..2) {
                try { trace += "b" } catch (caught: IllegalStateException) { trace += "wrong" }
                finally {
                    trace += "f"
                    if (index == 0) continue
                    throw failure
                }
            }
        } catch (caught: IllegalStateException) { observed = caught }
        assertTrue(observed === failure)
        assertEquals("bfbf", trace)
        trace = ""
        repeat(4) { index ->
            try {
                if (index == 0) return@repeat
                trace += "b"
            } finally { trace += "f" }
        }
        assertEquals("fbfbfbf", trace)
    }

    @TestAttribute
    fun nestedFinallyExitOverridesReturnAndRunsOuterCleanupOnce() {
        var trace = ""
        fun drive(): Int {
            outer@ for (index in 0..2) {
                try {
                    try { return 7 } finally {
                        trace += "i"
                        if (index == 0) continue@outer
                        break@outer
                    }
                } finally { trace += "o" }
            }
            return 9
        }
        assertEquals(9, drive())
        assertEquals("ioio", trace)
        fun routed(stop: Boolean): Int {
            for (index in 0..0) {
                try { return 17 } finally {
                    trace += "f"
                    if (stop) break
                }
            }
            return 19
        }
        trace = ""
        assertEquals(17, routed(false))
        assertEquals(19, routed(true))
        assertEquals("ff", trace)
    }

    @TestAttribute
    fun cfgAndEnumerationLoopExits() {
        var trace = ""
        var index = 0
        outer@ while (index < 4) {
            index++
            try {
                for (inner in listOf(0, 1, 2)) {
                    try {
                        if (inner == 0) continue
                        if (index == 1) continue@outer
                        if (index == 3) break@outer
                        trace += "b"
                        break
                    } finally { trace += "i" }
                }
            } finally { trace += "o" }
        }
        assertEquals("iioibioiio", trace)
        trace = ""
        index = 0
        do {
            index++
            try {
                if (index == 1) continue
                if (index == 3) break
                trace += "b"
            } finally { trace += "f" }
        } while (index < 4)
        assertEquals("fbff", trace)
    }
}
