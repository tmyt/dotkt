import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.AreSame as assertSame

private var unitValueTrace = ""
private var unitStoredValue: Any? = null
private fun unitValueEffect(tag: String, fail: Boolean = false) {
    unitValueTrace += tag
    if (fail) throw IllegalStateException(tag)
}
private fun unitReturnedValue(): Any = unitValueEffect("r")
private fun <T> unitIdentity(value: T): T = value
private class UnitValueBox(val value: Any?)
private class UnitValueReceiver {
    fun write() { unitValueEffect("m") }
}
private fun unitValueReceiver(): UnitValueReceiver {
    unitValueEffect("c")
    return UnitValueReceiver()
}
private fun unitValueTag(tag: String): String {
    unitValueEffect(tag)
    return tag
}
private fun unitValueArguments(left: String, value: Any?, right: String): String {
    assertSame(Unit, value)
    return left + right
}
private fun unitDiscardedCall() { unitValueEffect("d") }

interface UnitCallSource<T> { fun get(): T }
class UnitCallSourceValue<T>(private val value: T) : UnitCallSource<T> {
    override fun get(): T = value
}
fun <S : UnitCallSource<Unit>> unitConstrainedValue(source: S): Any = source.get()
private class UnitNullReceiverDelegate {
    operator fun getValue(thisRef: Any?, property: kotlin.reflect.KProperty<*>): Any? = thisRef
}

class UnitCallValueTests {
    @TestAttribute
    fun unitCallsSupplyValuesToStorageReturnsAndArguments() {
        unitValueTrace = ""
        var local: Any? = unitValueEffect("i")
        assertSame(Unit, local)
        local = unitValueEffect("s")
        assertSame(Unit, local)
        unitStoredValue = unitValueEffect("f")
        assertSame(Unit, unitStoredValue)
        assertSame(Unit, unitReturnedValue())
        assertSame(Unit, UnitValueBox(unitValueEffect("n")).value)
        val array = arrayOf<Any?>(unitValueEffect("a"))
        array[0] = unitValueEffect("w")
        assertSame(Unit, array[0])
        assertSame(Unit, unitIdentity(unitValueEffect("g")))
        assertEquals("isfrnawg", unitValueTrace)
    }

    @TestAttribute
    fun castsReceiversAndDelegatesConsumeUnitWithoutRepeatingCalls() {
        unitValueTrace = ""
        assertSame(Unit, unitValueEffect("x") as Any)
        assertEquals(true, unitValueEffect("e") == Unit)
        assertSame(Unit, unitValueReceiver().write())
        val action: () -> Unit = { unitValueEffect("l") }
        assertSame(Unit, action())
        // This generic call physically returns a Unit value, not void.
        assertSame(Unit, unitIdentity(Unit))
        assertSame(Unit, unitConstrainedValue(UnitCallSourceValue(Unit)))
        assertEquals("xecml", unitValueTrace)
    }

    @TestAttribute
    fun conditionalsMaterializeOnlyTheTakenPath() {
        unitValueTrace = ""
        fun choose(flag: Boolean): Any = if (flag) unitValueEffect("t") else unitValueEffect("f")
        assertSame(Unit, choose(true))
        assertSame(Unit, choose(false))
        assertEquals("tf", unitValueTrace)
        val absent: Unit? = null
        assertEquals(null, absent)
        val delegated by UnitNullReceiverDelegate()
        assertEquals(null, delegated)
        assertSame(Unit, run { })
        assertSame(Unit, check(true))
        val missing: String? = null
        var rejected = false
        try { requireNotNull(missing) } catch (error: IllegalArgumentException) { rejected = true }
        assertEquals(true, rejected)
    }

    @TestAttribute
    fun argumentOrderExceptionsAndDiscardedCallsArePreserved() {
        unitValueTrace = ""
        assertEquals("ab", unitValueArguments(unitValueTag("a"), unitValueEffect("u"), unitValueTag("b")))
        try {
            unitValueArguments(unitValueTag("c"), unitValueEffect("x", true), unitValueTag("never"))
        } catch (error: IllegalStateException) {
            assertEquals("x", error.message)
        }
        unitDiscardedCall()
        assertEquals("aubcxd", unitValueTrace)
    }
}
