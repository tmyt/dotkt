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
        // Isolate invocation conversion from the separately tracked function-type nullability import bug (#873).
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
        check(throughTyped(43, intIdentity()) == 43)
        val boxed: Any = 42
        check(objectIdentity()(boxed) === boxed)
        check((objectIdentity()(NumberValue(19)) as NumberValue).raw == 19)
        check(zeroArguments()() == 51)
        check(extensionPredicate()(42, "consumer"))
    }
}
