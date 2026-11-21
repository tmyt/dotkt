package roundtrip.projectedpropertyowner.consumer

import NUnit.Framework.TestAttribute
import roundtrip.projectedpropertyowner.Source
import roundtrip.projectedpropertyowner.read
import roundtrip.projectedpropertyowner.readNullable

class Box<out T>(val item: T)

private class Holder : Source<Box<*>> {
    override val value: Box<*> = Box("present")
    override val maybe: Box<*>? get() = value
}

class ProjectedPropertyOwnerTests {
    @TestAttribute
    fun referencedInterfaceAccessorsUseTheReachedPhysicalOwner() {
        val holder = Holder()
        check(read(holder) === holder.value)
        check(readNullable(holder) === holder.value)
        check(read(holder).item == "present")
    }
}
