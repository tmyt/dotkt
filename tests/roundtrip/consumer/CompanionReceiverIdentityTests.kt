import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.companionreceiver.NamedHost
import roundtrip.companionreceiver.DefaultHost
import roundtrip.companionreceiver.OtherNamedHost
import roundtrip.companionreceiver.GenericHost
import roundtrip.companionreceiver.OuterHost
import roundtrip.companionreceiver.CompanionBox
import roundtrip.companionreceiver.selected
import roundtrip.companionreceiver.label
import roundtrip.companionreceiver.keep
import roundtrip.companionreceiver.namedValue
import roundtrip.companionreceiver.namedValues
import roundtrip.externalcompanionreceiver.externalSelected
import roundtrip.externalcompanionreceiver.externalLabel

class CompanionReceiverIdentityTests {
    @TestAttribute
    fun importedCompanionClassifiersMatchTheirOwnReceivers() {
        assertEquals(11, NamedHost.selected(10))
        assertEquals(12, DefaultHost.selected(10))
        assertEquals(13, OtherNamedHost.selected(10))
        assertEquals(14, GenericHost.selected(10))
        assertEquals(15, OuterHost.NestedHost.selected(10))
        assertEquals("named", NamedHost.label)
        assertEquals("kept", NamedHost.keep("kept"))
        val receiver: NamedHost.Named = NamedHost.Named
        assertEquals(21, receiver.selected(20))
        assertTrue(namedValue() === receiver)
        assertTrue(namedValues()[0] === receiver)
        assertTrue(CompanionBox(receiver).value === receiver)
    }

    @TestAttribute
    fun extensionDllUsesTheDefiningDllCompanionIdentity() {
        assertEquals(21, NamedHost.externalSelected(10))
        assertEquals(22, DefaultHost.externalSelected(10))
        assertEquals(24, GenericHost.externalSelected(10))
        assertEquals("external", NamedHost.externalLabel)
    }
}
