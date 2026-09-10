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

private fun unitTryConditionalReturn(stop: Boolean): Any {
    return try {
        if (stop) return "early"
        unitTryEffect("b")
    } finally { unitTryEffect("f") }
}

private fun unitTryNonLocal(stop: Boolean): Any {
    val value: Any = try {
        listOf(1, 2).forEach {
            if (stop && it == 2) return "found"
            unitTryEffect("v")
        }
    } finally { unitTryEffect("n") }
    return value
}

private inline fun <T> unitTryGeneric(block: () -> T): T = try { block() } finally { unitTryEffect("g") }

private fun unitTryNestedValue(fail: Boolean): Any? = try {
    val inner: Any? = try {
        if (fail) throw IllegalArgumentException("null")
        unitTryEffect("j")
    } catch (error: IllegalArgumentException) { null }
    inner
} finally { unitTryEffect("o") }

private fun unitTryMixedValue(mode: Int): Any? = try {
    if (mode == 1) throw IllegalArgumentException("unit")
    if (mode == 2) throw IllegalStateException("null")
    42
} catch (error: IllegalArgumentException) {
} catch (error: IllegalStateException) {
    null
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

    @TestAttribute
    fun conditionalAndInlineReturnsLeaveThroughFinally() {
        unitTryTrace = ""
        assertEquals("early", unitTryConditionalReturn(true))
        assertSame(Unit, unitTryConditionalReturn(false))
        assertEquals("fbf", unitTryTrace)
        unitTryTrace = ""
        assertEquals("found", unitTryNonLocal(true))
        assertSame(Unit, unitTryNonLocal(false))
        assertEquals("vnvvn", unitTryTrace)
        unitTryTrace = ""
        assertSame(Unit, unitTryGeneric { unitTryEffect("i") })
        assertSame(Unit, unitTryGeneric { return@unitTryGeneric Unit })
        assertEquals("igg", unitTryTrace)
    }

    @TestAttribute
    fun nestedNullableJoinsAndMixedCatchResultsKeepTheirValues() {
        unitTryTrace = ""
        assertSame(Unit, unitTryNestedValue(false))
        assertEquals(null, unitTryNestedValue(true))
        assertEquals("joo", unitTryTrace)
        assertEquals(42, unitTryMixedValue(0))
        assertSame(Unit, unitTryMixedValue(1))
        assertEquals(null, unitTryMixedValue(2))
    }
}
