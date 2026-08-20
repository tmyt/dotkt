import Probe.IMemberConstraintSlot

interface LocalMemberConstraintSlot : IMemberConstraintSlot

fun invalidInheritedMemberConstraint(slot: LocalMemberConstraintSlot): Int = slot.Reference(1)
