package roundtrip.defaultpropertyslot

import RoundtripPropertyInterop.IPropertySlot
import RoundtripPropertyInterop.IGenericPropertySlot
import RoundtripPropertyInterop.IEmptyDefaultSlot

// The private CLR MethodImpl bridges from this referenced Kotlin interface to IPropertySlot are the only physical
// record of the external get_value/set_value slots. A downstream Kotlin class receives the selected default body as
// a frontend fact and must not make bir2cir rediscover that semantic decision from this assembly's method bodies.
interface ReferencedDefaultPropertySlot : IPropertySlot {
    override var value: Int
        get() = 310
        set(next) {}
}

interface ReferencedGenericDefaultPropertySlot<T> : IGenericPropertySlot<T> {
    override var value: T
        get() = defaultValue()
        set(next) {}

    fun defaultValue(): T
}

// A downstream override must reuse the external MethodImpl already owned by this referenced base. Re-emitting the
// same interface mapping on the derived accessor creates competing CoreCLR interface-map entries.
open class ReferencedPropertySlotBase : IPropertySlot {
    open override var value: Int = 330
}

// Empty Unit bodies are still concrete default implementations. Their declaration modality, not statement count,
// must drive both IL body emission and exact base-interface MethodImpl wiring.
interface ReferencedEmptyDefaultSlot : IEmptyDefaultSlot {
    override fun touch() {}
}

// DeclarationRename assigns CompareTo physically, but a consuming frontend must continue to see the Kotlin source
// identity compareTo. The explicit source-method carrier is the cross-module edge between those two facts.
interface ReferencedRenamedDefaultMethodSlot : Comparable<ReferencedRenamedDefaultMethodSlot> {
    override fun compareTo(other: ReferencedRenamedDefaultMethodSlot): Int = 360
}

// The two declarations share source name, method arity and nullable-generic first parameter. A downstream override's
// frontend edge identifies the exact sibling by the complete signature; the reference reader must preserve that edge
// instead of refusing the overload set and leaving both CLR slots unimplemented.
interface ReferencedNullableOverloadSlot<T> {
    fun choose(value: T?, marker: String): Int
    fun choose(value: T?, marker: Int): Int
}
