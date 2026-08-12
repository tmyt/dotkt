package roundtrip.covariantdefaultpropertyslot

import RoundtripPropertyInterop.IReadOnlyNominalPropertySlot
import RoundtripPropertyInterop.PropertySlotDerivedValue

// This default uses a private signature-changing MethodImpl bridge for the external base-returning getter. A
// downstream class must receive that exact bridge identity from reference metadata rather than compare its physical
// signature with this derived-returning source accessor.
interface ReferencedCovariantDefaultPropertySlot : IReadOnlyNominalPropertySlot {
    override val value: PropertySlotDerivedValue
        get() = PropertySlotDerivedValue("referenced-covariant-property")
}
