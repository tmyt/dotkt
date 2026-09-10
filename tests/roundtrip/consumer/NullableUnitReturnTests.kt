import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.AreSame as assertSame
import nullableunitreturns.*

private class NullableUnitDerived : Base() {
    override fun get(present: Boolean): Unit? = if (present) Unit else null
}
private class NonNullUnitDerived : Base() {
    override fun get(present: Boolean) {}
}

class NullableUnitReturnRoundtripTests {
    @TestAttribute
    fun importedNullableUnitResultsAndStorageKeepTheirSurface() {
        assertSame(Unit, optionalUnit(true))
        assertEquals(null, optionalUnit(false))
        val holder = Holder(Unit)
        assertSame(Unit, holder.value)
        holder.value = null
        assertEquals(null, holder.value)
        val values: List<Unit?> = optionalList()
        assertSame(Unit, values[0])
        assertEquals(null, values[1])
    }

    @TestAttribute
    fun importedFunctionValuesDistinguishUnitFromVoid() {
        val f: (Boolean) -> Unit? = callback()
        assertSame(Unit, f(true))
        assertEquals(null, f(false))
        val a: () -> Unit = action()
        assertSame(Unit, a())
        assertSame(Unit, identity(Unit))
        assertEquals(null, identity<Unit?>(null))
    }

    @TestAttribute
    fun importedNullableFunctionDeclarationSlotsKeepValues() {
        val holder = CallbackHolder { present -> if (present) Unit else null }
        assertSame(Unit, invokeCallback(holder.callback, true))
        assertEquals(null, invokeCallback(holder.callback, false))
        holder.callback = { }
        assertSame(Unit, invokeCallback(holder.callback, false))
        val original = unitCallback()
        holder.callback = original
        assertSame(Unit, invokeCallback(holder.callback, false))
        assertSame(Unit, invokeCallback(original, true))
    }

    @TestAttribute
    fun inheritedInterfaceSlotsKeepExactReturns() {
        val derived = NullableUnitDerived()
        val base: Base = derived
        val source: Source = derived
        assertSame(Unit, base.get(true))
        assertEquals(null, base.get(false))
        assertSame(Unit, source.get(true))
        assertEquals(null, source.get(false))
        val nonNull: Source = NonNullUnitDerived()
        assertSame(Unit, nonNull.get(false))
    }
}
