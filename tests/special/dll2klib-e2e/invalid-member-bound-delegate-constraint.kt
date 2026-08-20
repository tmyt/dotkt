import Probe.MemberConstraintHost

val invalidMemberBoundDelegateConstraint: (String) -> Int = MemberConstraintHost()::Struct
