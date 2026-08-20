import Probe.IMemberConstraintSlot

fun <T : IMemberConstraintSlot> invalidConstrainedMemberConstraint(slot: T): Int = slot.Reference(1)
