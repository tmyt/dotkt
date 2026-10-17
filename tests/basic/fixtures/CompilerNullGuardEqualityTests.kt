import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

class CompilerNullGuardValue {
    var calls = 0
    override fun equals(other: Any?): Boolean { calls++; return other is CompilerNullGuardValue }
    override fun hashCode(): Int = 1
}

private fun <T> compilerGuardStore(values: Array<T>, value: T) { values[0] = value }
private inline fun <reified T> compilerGuardPredicate(): (Any?) -> Boolean = { it is T }
private inline fun <reified T> compilerGuardCast(): (Any?) -> T? = { it as? T }

class CompilerNullGuardEqualityTests {
    @TestAttribute
    fun genericArrayStoreDoesNotCallValueEqualsForNullGuards() {
        val value = CompilerNullGuardValue()
        val values = arrayOf(value)
        compilerGuardStore(values, value)
        assertEquals(true, values[0] === value)
        assertEquals(0, value.calls)
    }

    @TestAttribute
    fun capturedReifiedTypeTestsAndSafeCastsDoNotCallEquals() {
        val value = CompilerNullGuardValue()
        val predicate = compilerGuardPredicate<CompilerNullGuardValue?>()
        val cast = compilerGuardCast<CompilerNullGuardValue?>()
        assertEquals(true, predicate(value))
        assertEquals(true, predicate(null))
        assertEquals(true, cast(value) === value)
        assertEquals(true, cast(null) === null)
        assertEquals(0, value.calls)
    }

    @TestAttribute
    fun authoredStructuralEqualityStillInvokesTheUserOverride() {
        val left = CompilerNullGuardValue()
        val right = CompilerNullGuardValue()
        assertEquals(true, left == right)
        assertEquals(1, left.calls)
        assertEquals(false, left.equals(null))
        assertEquals(2, left.calls)
        assertEquals(false, left === right)
        assertEquals(2, left.calls)
    }
}
