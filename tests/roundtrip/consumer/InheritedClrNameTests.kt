import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.AreSame as assertSame
import inheritedclrnames.*
import mpp.app.InheritedInterfaceSlots

private class ConsumedNamedString : StringBody(), StringSlot, ValueSlot<String>
private class ConsumedNamedOwner : OwnerBody<String>("constructed"), ValueSlot<String>
private class ConsumedNamedMethod : MethodBody(), MethodSlot
private class ConsumedNamedBound : BoundBody<BoundValue>(), BoundSlot<BoundValue>
private class ConsumedNamedUnit : UnitBody(), ValueSlot<Unit>
private class ConsumedProperty : PropertyBody(), PropertySlot
private class ConsumedMutable : MutableBody<String>("before"), MutableSlot<String>
private class ConsumedExtensionDefault : IntExtensionDefault, TaggedExtensionSlot<Int>

class InheritedClrNameRoundtripTests {
    @TestAttribute
    fun inheritedInterfaceMembersDispatchAcrossThreeAssemblies() {
        val throughMid: InheritedInterfaceSlots = object : InheritedInterfaceSlots {
            override val tag: String get() = "property"
            override fun read(): String = "method"
        }
        assertEquals("property", throughMid.tag)
        assertEquals("method", throughMid.read())
    }

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
        val tagged: TaggedExtensionSlot<Int> = ConsumedExtensionDefault()
        val cell = ExtensionCell<String>(9)
        assertEquals(9, tagged.exchange(cell, 42))
        assertEquals(42, cell.n)
        with(tagged) {
            cell.value = 73
            assertEquals(73, cell.value)
        }
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
