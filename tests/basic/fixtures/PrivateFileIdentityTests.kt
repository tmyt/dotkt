import NUnit.Framework.TestAttribute
import privatefileidentity.probe.*

class PrivateFileIdentityTests {
    @TestAttribute
    fun selectedPrivateFunctionsSurviveDirectDefaultAndReferenceCalls() {
        val leftBefore = leftFilePrivateReads()
        val rightBefore = rightFilePrivateReads()
        check(leftFilePrivateDirect() == 17)
        check(rightFilePrivateDirect() == 23)
        check(leftFilePrivateDefault() == 31)
        check(rightFilePrivateDefault() == 37)
        check(leftFilePrivateReference()() == 43)
        check(rightFilePrivateReference()() == 47)
        check(leftFilePrivateReads() == leftBefore + 3)
        check(rightFilePrivateReads() == rightBefore + 3)
    }

    @TestAttribute
    fun privateGetterIdentitiesRemainSourceFileScoped() {
        check(leftFilePrivateValue() == 107)
        check(rightFilePrivateValue() == 211)
    }
}
