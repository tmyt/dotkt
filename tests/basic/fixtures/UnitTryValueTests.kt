import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.AreSame as assertSame

private var unitTryTrace = ""
private fun unitTryEffect(tag: String) { unitTryTrace += tag }

private fun unitTryBranchValue(fail: Boolean): Any = try {
    if (fail) throw IllegalArgumentException("branch")
    unitTryEffect("t")
} catch (error: IllegalArgumentException) {
    unitTryEffect("c")
} finally {
    unitTryEffect("f")
}

private fun unitTryNullableBranch(fail: Boolean): Any? = try {
    if (fail) throw IllegalArgumentException("null branch")
    Unit
} catch (error: IllegalArgumentException) {
    null
}

private fun unitTryEarlyReturn(): Any {
    val ignored: Any = try {
        return "early"
    } finally {
        unitTryEffect("r")
    }
    return ignored
}

class UnitTryValueTests {
    @TestAttribute
    fun normalAndCatchBranchesSupplyUnitBeforeFinally() {
        unitTryTrace = ""
        assertSame(Unit, unitTryBranchValue(false))
        assertSame(Unit, unitTryBranchValue(true))
        assertEquals("tfcf", unitTryTrace)
    }

    @TestAttribute
    fun emptyDeclarationAndNestedBranchesSupplyUnit() {
        unitTryTrace = ""
        val empty: Any = try { } finally { unitTryEffect("e") }
        val declaration: Any = try { val unused = unitTryEffect("d") } finally { unitTryEffect("f") }
        val nested: Any = try {
            if (true) try { unitTryEffect("n") } finally { unitTryEffect("i") } else Unit
        } finally { unitTryEffect("o") }
        assertSame(Unit, empty)
        assertSame(Unit, declaration)
        assertSame(Unit, nested)
        assertEquals("edfnio", unitTryTrace)
    }

    @TestAttribute
    fun nullRemainsNullAndNothingDoesNotCompleteTheJoin() {
        assertSame(Unit, unitTryNullableBranch(false))
        assertEquals(null, unitTryNullableBranch(true))
        val literalNull: Any? = try { null } finally { }
        assertEquals(null, literalNull)
        unitTryTrace = ""
        assertEquals("early", unitTryEarlyReturn())
        assertEquals("r", unitTryTrace)
    }

    @TestAttribute
    fun finallyExceptionsAndArgumentOrderRemainObservable() {
        unitTryTrace = ""
        val values = arrayOf<Any?>(unitTryEffect("a"), try { unitTryEffect("b") } finally { unitTryEffect("c") }, unitTryEffect("d"))
        for (value in values) assertSame(Unit, value)
        assertEquals("abcd", unitTryTrace)
        var caught = false
        try {
            val unreachable: Any = try { unitTryEffect("t") } finally { throw IllegalStateException("finally") }
            assertEquals("never", unreachable)
        } catch (error: IllegalStateException) {
            caught = error.message == "finally"
        }
        assertEquals(true, caught)
        assertEquals("abcdt", unitTryTrace)
    }
}
