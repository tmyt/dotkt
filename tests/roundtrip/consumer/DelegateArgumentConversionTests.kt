package roundtriptests.delegateargumentconversion

import NUnit.Framework.TestAttribute
import roundtrip.delegateargumentconversion.*

class DelegateArgumentConversionTests {
    @TestAttribute
    fun localDelegateArgumentsUsePhysicalInvokeSlots() {
        verifyLocalDelegateArguments()
    }

    @TestAttribute
    fun importedDelegateArgumentsUsePhysicalInvokeSlots() {
        check(objectPredicate()(42))
        check(objectIdentity()(37) as Int == 37)
        check(objectIdentity()(false) as Boolean == false)
        val nullableIdentity = objectIdentity() as (Any?) -> Any?
        val value: Int? = 11
        val absent: Int? = null
        check(nullableIdentity(value) as Int == 11)
        check(nullableIdentity(absent) == null)
        check(objectIdentity()(Marker.Value) == Marker.Value)
        val token = Token()
        check(objectIdentity()(token) === token)
        check(referenceIdentity()(token) === token)
        check(intIdentity()(31) == 31)
        check(throughObject(41) as Int == 41)
        check(extensionPredicate()(42, "consumer"))
    }
}
