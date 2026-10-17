import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

class CompilerNullGuardValue(val matchesEverything: Boolean = false) {
    var calls = 0
    override fun equals(other: Any?): Boolean { calls++; return matchesEverything || other is CompilerNullGuardValue }
    override fun hashCode(): Int = 1
}

private fun <T> compilerGuardStore(values: Array<T>, value: T) { values[0] = value }
private inline fun <reified T> compilerGuardPredicate(): (Any?) -> Boolean = { it is T }
private inline fun <reified T> compilerGuardCast(): (Any?) -> T? = { it as? T }
private inline fun <reified T> compilerGuardCheckedCast(): (Any?) -> T = { it as T }

private class CompilerNullGuardMap(private val storedKey: CompilerNullGuardValue) :
    AbstractMap<CompilerNullGuardValue, String>() {
    var calls = 0
    override val entries: Set<Map.Entry<CompilerNullGuardValue, String>>
        get() = setOf(object : Map.Entry<CompilerNullGuardValue, String> {
            override val key: CompilerNullGuardValue get() = storedKey
            override val value: String get() = "hit"
        })
    override fun get(key: CompilerNullGuardValue): String? {
        calls++
        return if (key === storedKey) "hit" else null
    }
}

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
        val checkedCast = compilerGuardCheckedCast<CompilerNullGuardValue?>()
        assertEquals(true, predicate(value))
        assertEquals(true, predicate(null))
        assertEquals(true, cast(value) === value)
        assertEquals(true, cast(null) === null)
        assertEquals(true, checkedCast(value) === value)
        assertEquals(true, checkedCast(null) === null)
        assertEquals(0, value.calls)
    }

    @TestAttribute
    fun erasedMapQueryDoesNotTreatAnEqualKeyAsNull() {
        val key = CompilerNullGuardValue(matchesEverything = true)
        val implementation = CompilerNullGuardMap(key)
        val map: Map<CompilerNullGuardValue, String> = implementation
        assertEquals("hit", map[key])
        assertEquals(1, implementation.calls)
        assertEquals(0, key.calls)
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
