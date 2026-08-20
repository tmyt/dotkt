import Probe.MemberConstraintApi

fun <T> invalidOpenMemberClassConstraint(value: T): Int = MemberConstraintApi.Reference(value)
