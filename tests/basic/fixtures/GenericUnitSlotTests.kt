import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.AreSame as assertSame

private interface GenericUnitSlot<T> { fun get(): T }
private interface PlainUnitSlot { fun get() }
private interface IndirectUnitSlot<T> : GenericUnitSlot<T>
private var genericUnitEffects = 0
private class DirectUnitSlot : GenericUnitSlot<Unit>, PlainUnitSlot {
    override fun get() { genericUnitEffects++ }
}
private open class PlainUnitBody {
    open fun get() { genericUnitEffects += 10 }
}
private open class InheritedUnitSlot : PlainUnitBody(), GenericUnitSlot<Unit>
private class FurtherUnitSlot : InheritedUnitSlot() {
    override fun get() { genericUnitEffects += 100 }
}
private class IndirectUnitBody : IndirectUnitSlot<Unit> {
    override fun get() { genericUnitEffects++ }
}
private abstract class GenericUnitBase<T> { abstract fun get(): T }
private open class UnitBaseBody : GenericUnitBase<Unit>(), GenericUnitSlot<Unit> {
    override fun get() { genericUnitEffects++ }
}
private class FurtherUnitBase : UnitBaseBody() {
    override fun get() { genericUnitEffects += 10 }
}
private interface DefaultUnitSlot : GenericUnitSlot<Unit> {
    override fun get() { genericUnitEffects++ }
}
private class DefaultUnitBody : DefaultUnitSlot
private class OverrideUnitDefault : DefaultUnitSlot {
    override fun get() { genericUnitEffects += 10 }
}
private class GenericUnitOwner<T>(private val value: T) : GenericUnitSlot<T> {
    override fun get(): T = value
}

class GenericUnitSlotTests {
    @TestAttribute
    fun genericAndOrdinaryUnitSlotsShareTheSourceBody() {
        genericUnitEffects = 0
        val source = DirectUnitSlot()
        val generic: GenericUnitSlot<Unit> = source
        val plain: PlainUnitSlot = source
        assertSame(Unit, generic.get())
        assertSame(Unit, plain.get())
        assertSame(Unit, source.get())
        assertEquals(3, genericUnitEffects)
    }

    @TestAttribute
    fun inheritedBodiesAndIndirectInterfacesRetainVirtualDispatch() {
        genericUnitEffects = 0
        val inherited: GenericUnitSlot<Unit> = InheritedUnitSlot()
        val further: GenericUnitSlot<Unit> = FurtherUnitSlot()
        val indirect: GenericUnitSlot<Unit> = IndirectUnitBody()
        assertSame(Unit, inherited.get())
        assertSame(Unit, further.get())
        assertSame(Unit, indirect.get())
        assertEquals(111, genericUnitEffects)
    }

    @TestAttribute
    fun genericBaseAndInterfaceReturnsStayValueBearing() {
        genericUnitEffects = 0
        val source = FurtherUnitBase()
        val base: GenericUnitBase<Unit> = source
        val generic: GenericUnitSlot<Unit> = source
        assertSame(Unit, base.get())
        assertSame(Unit, generic.get())
        assertSame(Unit, source.get())
        assertEquals(30, genericUnitEffects)
    }

    @TestAttribute
    fun defaultInterfaceBodiesAndOverridesFillGenericSlots() {
        genericUnitEffects = 0
        val default: GenericUnitSlot<Unit> = DefaultUnitBody()
        val overridden: GenericUnitSlot<Unit> = OverrideUnitDefault()
        assertSame(Unit, default.get())
        assertSame(Unit, overridden.get())
        assertEquals(11, genericUnitEffects)
    }

    @TestAttribute
    fun nullableUnitAndOtherGenericResultsRemainValues() {
        val absent: GenericUnitSlot<Unit?> = GenericUnitOwner(null)
        val present: GenericUnitSlot<Unit?> = GenericUnitOwner(Unit)
        val number: GenericUnitSlot<Int> = GenericUnitOwner(42)
        val text: GenericUnitSlot<String> = GenericUnitOwner("value")
        assertEquals(null, absent.get())
        assertSame(Unit, present.get())
        assertEquals(42, number.get())
        assertEquals("value", text.get())
    }

    @TestAttribute
    fun uninstantiatedGenericOwnerReturnsKeepTheirFrame() {
        val owner = GenericUnitOwner(Unit)
        val generic: GenericUnitSlot<Unit> = owner
        assertSame(Unit, owner.get())
        assertSame(Unit, generic.get())
    }
}
