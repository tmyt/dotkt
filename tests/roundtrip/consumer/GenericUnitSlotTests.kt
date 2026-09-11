import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.AreSame as assertSame
import genericunitslots.*

private class ConsumerDerivedUnit : DerivedSource<Unit> {
    override fun get() { effects++ }
}
private class ConsumerFixedUnit : FixedUnitSource {
    override fun get() { effects += 10 }
}
private open class ConsumerInheritedUnit : PlainBody(), Source<Unit>
private class ConsumerFurtherInheritedUnit : ConsumerInheritedUnit() {
    override fun get() { effects += 100 }
}
private class ConsumerGenericBase : GenericBase<Unit>() {
    override fun get() { effects++ }
}
private class ConsumerFurtherUnit : UnitBase() {
    override fun get() { effects += 10 }
}
private class ConsumerUnitDefault : UnitDefault
private class ConsumerOverrideDefault : UnitDefault {
    override fun get() { effects += 10 }
}

class GenericUnitSlotRoundtripTests {
    @TestAttribute
    fun importedImplementationsRetainUnitValueAndVoidSlots() {
        effects = 0
        val source = UnitSource()
        val generic: Source<Unit> = source
        val plain: PlainSource = source
        assertSame(Unit, generic.get())
        assertSame(Unit, plain.get())
        assertSame(Unit, source.get())
        val inherited: Source<Unit> = InheritedBody()
        assertSame(Unit, inherited.get())
        assertEquals(13, effects)
        val owner: Source<Unit> = GenericOwner(Unit)
        assertSame(Unit, owner.get())
        val absent: Source<Unit?> = GenericOwner(null)
        assertEquals(null, absent.get())
    }

    @TestAttribute
    fun localImplementationsCloseImportedGenericAndFixedInterfaces() {
        effects = 0
        val derived: Source<Unit> = ConsumerDerivedUnit()
        val fixed: Source<Unit> = ConsumerFixedUnit()
        assertSame(Unit, derived.get())
        assertSame(Unit, fixed.get())
        val inherited: Source<Unit> = ConsumerInheritedUnit()
        val further: Source<Unit> = ConsumerFurtherInheritedUnit()
        assertSame(Unit, inherited.get())
        assertSame(Unit, further.get())
        assertEquals(121, effects)
    }

    @TestAttribute
    fun importedGenericBaseBridgesPreserveFurtherOverrides() {
        effects = 0
        val genericBase: GenericBase<Unit> = ConsumerGenericBase()
        val further = ConsumerFurtherUnit()
        val base: GenericBase<Unit> = further
        val source: Source<Unit> = further
        assertSame(Unit, genericBase.get())
        assertSame(Unit, base.get())
        assertSame(Unit, source.get())
        assertSame(Unit, further.get())
        assertEquals(31, effects)
    }

    @TestAttribute
    fun importedDefaultInterfaceBodiesKeepGenericSlotsSatisfied() {
        effects = 0
        val producer: Source<Unit> = DefaultBody()
        val consumer: Source<Unit> = ConsumerUnitDefault()
        val overridden: Source<Unit> = ConsumerOverrideDefault()
        assertSame(Unit, producer.get())
        assertSame(Unit, consumer.get())
        assertSame(Unit, overridden.get())
        assertEquals(12, effects)
    }
}
