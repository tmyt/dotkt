import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.publishedcallables.*
import InheritedCallableAdapter.*

private class PublishedVisibilityChild : Derived() {
    fun updateProtectedField(): Int {
        protectedField = 14
        return protectedField
    }
}

private fun publishedNonLocalReturn(): Int {
    publicSplice { return it }
    return -1
}

class PublishedCallableVisibilityTests {
    @TestAttribute
    fun inheritedPublicMembersAndExplicitInterfaceSlotsRemainDistinct() {
        val value = PublishedVisibilityChild()
        check(value.publicValue == 11)
        value.publicField = 15
        check(value.publicField == 15)
        check(value.updateProtectedField() == 14)
        check((value as ISecret).secret == "property-slot")
        check((value as ISecretField).secretField == "field-slot")
        check((value as IPublicValue).publicValue == "public-property-slot")
        check((value as IPublicField).publicField == "public-field-slot")
    }

    @TestAttribute
    fun publicEntryAndDefaultRemainCallableAcrossDlls() {
        check(publicEntry() == 7)
        check(publicDefault() == 7)
        check(publicDefault(17) == 17)
    }

    @TestAttribute
    fun actualInlineBodyCallsThePublicPhysicalHelperWithoutPublishingKotlinAccess() {
        check(publicSplice { it + 1 } == 8)
        check(publishedNonLocalReturn() == 7)
        val value = BoundValue()
        check(publicConstrained(value) { it } === value)
    }

    @TestAttribute
    fun propertiesAndConstructorsRemainUsableFromConsumerInlineBodies() {
        check(publicProperties { it } == 69)
        check(publicGeneric(17) { it } == 17)
        check(publicGeneric("generic") { it } == "generic")
        check(publicGenericProperty(19) { it } == 19)
        check(publicGenericProperty("property") { it } == "property")
        check(publicStatics { it } == 43)
    }

    @TestAttribute
    fun suspendBridgeRetainsTheCallableDeclarationVisibility() {
        var value = 0
        var complete = false
        val operation: suspend () -> Int = { publicSuspended() }
        operation.startCoroutine(object : Continuation<Int> {
            override val context: CoroutineContext get() = EmptyCoroutineContext
            override fun resumeWith(result: Result<Int>) {
                value = result.getOrThrow()
                complete = true
            }
        })
        check(complete && value == 7)
    }
}
