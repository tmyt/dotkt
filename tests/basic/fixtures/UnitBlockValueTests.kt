import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.AreSame as assertSame

private var unitBlockTrace = ""
private fun unitBlockEffect(tag: String): Int { unitBlockTrace += tag; return 1 }
private fun unitBlockDeclaration(flag: Boolean): Any = if (flag) {
    unitBlockEffect("a")
    val ignored = unitBlockEffect("d")
} else {
    val ignored = unitBlockEffect("e")
}
private fun unitBlockSubject(mode: Int): Int { unitBlockEffect("s"); return mode }
private fun unitBlockWhen(mode: Int): Any = when (unitBlockSubject(mode)) {
    0 -> { val ignored = unitBlockEffect("w") }
    1 -> { }
    else -> { unitBlockEffect("i"); 42 }
}
private fun unitBlockReturn(flag: Boolean, stop: Boolean): Any = if (flag) {
    if (stop) return "early"
    val ignored = unitBlockEffect("r")
} else Unit

class UnitBlockValueTests {
    @TestAttribute
    fun declarationsKeepLeadingEffectsAndInitializersInBothBranches() {
        unitBlockTrace = ""
        assertSame(Unit, unitBlockDeclaration(true))
        assertSame(Unit, unitBlockDeclaration(false))
        assertEquals("ade", unitBlockTrace)
        unitBlockTrace = ""
        assertSame(Unit, unitBlockWhen(0))
        assertSame(Unit, unitBlockWhen(1))
        assertEquals(42, unitBlockWhen(2))
        assertEquals("swssi", unitBlockTrace)
    }

    @TestAttribute
    fun loopAndAssignmentTailsAreStatementsWithAUnitResult() {
        unitBlockTrace = ""
        var count = 0
        fun choose(flag: Boolean): Any = if (flag) {
            while (count < 2) { unitBlockEffect("l"); count++ }
        } else {
            count = 9
        }
        assertSame(Unit, choose(true))
        assertEquals(2, count)
        assertSame(Unit, choose(false))
        assertEquals(9, count)
        val doLoop: Any = if (count == 9) {
            do { unitBlockEffect("d"); count-- } while (count > 7)
        } else Unit
        assertSame(Unit, doLoop)
        assertEquals(7, count)
        assertEquals("lldd", unitBlockTrace)
    }

    @TestAttribute
    fun nestedBlocksPreserveCapturedLocalsAndEvaluationOrder() {
        unitBlockTrace = ""
        var captured = 0
        val values = arrayOf<Any>(
            unitBlockEffect("p"),
            if (captured == 0) {
                val add = { captured++ }
                add()
                val nested: Any = if (captured == 1) { val ignored = unitBlockEffect("n") } else Unit
                assertSame(Unit, nested)
                val ignored = unitBlockEffect("o")
            } else Unit,
            unitBlockEffect("q"),
        )
        assertSame(Unit, values[1])
        assertEquals(1, captured)
        assertEquals("pnoq", unitBlockTrace)
    }

    @TestAttribute
    fun initializerExceptionsAndReturnsDoNotReachLaterStatements() {
        unitBlockTrace = ""
        var caught = false
        try {
            val value: Any = if (!caught) {
                unitBlockEffect("x")
                val ignored = throw IllegalStateException("initializer")
            } else Unit
            unitBlockEffect("never")
        } catch (error: IllegalStateException) {
            caught = error.message == "initializer"
        }
        assertEquals(true, caught)
        assertEquals("x", unitBlockTrace)
        assertEquals("early", unitBlockReturn(true, true))
        assertSame(Unit, unitBlockReturn(true, false))
        assertSame(Unit, unitBlockReturn(false, true))
        assertEquals("xr", unitBlockTrace)
    }
}
