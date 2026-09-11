import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.AreSame as assertSame
import inheritedclrnames.*

private class ConsumedNamedString : StringBody(), StringSlot, ValueSlot<String>
private class ConsumedNamedOwner : OwnerBody<String>("constructed"), ValueSlot<String>
private class ConsumedNamedMethod : MethodBody(), MethodSlot
private class ConsumedNamedBound : BoundBody<BoundValue>(), BoundSlot<BoundValue>
private class ConsumedNamedUnit : UnitBody(), ValueSlot<Unit>
private class ConsumedProperty : PropertyBody(), PropertySlot
private class ConsumedMutable : MutableBody<String>("before"), MutableSlot<String>

class InheritedClrNameRoundtripTests {
    @TestAttribute
    fun importedInheritedFinalPropertiesKeepAccessorSlots() {
        val producer: PropertySlot = ProducedProperty()
        val consumer: PropertySlot = ConsumedProperty()
        assertEquals("property", producer.tag)
        assertEquals("property", consumer.tag)
        val body = ConsumedMutable()
        val mutable: MutableSlot<String> = body
        assertEquals("before", mutable.value)
        mutable.value = "after"
        assertEquals("after", body.value)
    }

    @TestAttribute
    fun producerAndConsumerMappingsKeepKotlinNames() {
        val producer: StringSlot = ProducedString()
        val consumer = ConsumedNamedString()
        val plain: StringSlot = consumer
        val generic: ValueSlot<String> = consumer
        assertEquals("producer", producer.read())
        assertEquals("producer", plain.read())
        assertEquals("producer", generic.read())
        assertEquals("producer", consumer.read())
    }

    @TestAttribute
    fun importedGenericFramesResolveRenamedDeclarations() {
        val owner: ValueSlot<String> = ConsumedNamedOwner()
        val method: MethodSlot = ConsumedNamedMethod()
        assertEquals("constructed", owner.read())
        assertEquals("method", method.identity("method"))
        assertEquals(42, method.identity(42))
        val bounded: BoundSlot<BoundValue> = ConsumedNamedBound()
        val produced: BoundSlot<BoundValue> = ProducedBound()
        val payload = BoundPayload()
        assertSame(payload, bounded.identity(payload))
        assertSame(payload, produced.identity(payload))
    }

    @TestAttribute
    fun importedEmptyUnitBodySuppliesRealUnit() {
        val source: ValueSlot<Unit> = ConsumedNamedUnit()
        assertSame(Unit, source.read())
    }
}
