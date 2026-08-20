import Probe.MemberConstraintApi

fun invalidMemberUnmanagedConstraint(): Int = MemberConstraintApi.Unmanaged("not unmanaged")
