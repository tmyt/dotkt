package roundtriptests.arrayfactoryidentity

import NUnit.Framework.TestAttribute
import roundtrip.arrayfactoryidentity.intArrayOf as userIntArrayOf
import roundtrip.arrayfactoryidentity.arrayOf as userArrayOf
import roundtrip.arrayfactoryidentity.arrayOfNulls as userArrayOfNulls
import roundtrip.arrayfactoryidentity.verifyLocalArrayFactoryIdentity

class ArrayFactoryIdentityTests {
    @TestAttribute
    fun localDeclarationsDoNotAcquireFactorySemantics() {
        verifyLocalArrayFactoryIdentity()
    }

    @TestAttribute
    fun referencedDeclarationsRetainTheirOwnBodies() {
        val custom = userIntArrayOf(4, 7)
        check(custom.size == 4 && custom[3] == 7)
        check(userArrayOf("consumer")[0] == "user:consumer")
        check(userArrayOfNulls(1)[0] == "initialized")
        val standard = intArrayOf(1, 2)
        check(standard.size == 2 && standard[0] == 1)
        check(arrayOfNulls<String>(1)[0] == null)
    }
}
