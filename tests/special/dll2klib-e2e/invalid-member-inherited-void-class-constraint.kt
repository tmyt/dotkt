import Probe.IInheritedVoidConstraintSlot

interface LocalVoidConstraintSlot : IInheritedVoidConstraintSlot

fun invalidInheritedVoidConstraint(slot: LocalVoidConstraintSlot) {
    slot.Accept(1)
}
