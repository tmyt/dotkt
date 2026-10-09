package roundtriptests.throwablesource

import NUnit.Framework.TestAttribute
import roundtrip.throwablesource.*

class ThrowableSourceIdentityTests {
    @TestAttribute
    fun kotlinExceptionAliasesRetainTheirDeclaredHierarchyAcrossDlls() {
        val error = IllegalStateException("expected")
        check(acceptThrowable(error) == "expected")
        check(echoException(error) === error)
        check(echoRuntimeException(error) === error)
        check(echoIllegalStateException(error) === error)
        val other = IllegalArgumentException("other")
        check(acceptThrowable(other) == "other")
        check(echoException(other) === other)
        check(error.sourceMessage() == "expected")
    }

    @TestAttribute
    fun nullableNestedAndMemberSlotsRetainThrowableIdentity() {
        val error = IllegalStateException("first")
        check(echoNullableThrowable(null) == null)
        check(echoNullableThrowable(error) === error)
        val errors = echoThrowableList(listOf(error))
        check(errors[0] === error)
        val holder = ErrorHolder(error)
        check(holder.error === error)
        val other = IllegalArgumentException("second")
        holder.error = other
        check(holder.error === other)
        check(holder.accept(other) == "second")
    }
}
