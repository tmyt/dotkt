import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.publishedcallables.*

private fun publishedNonLocalReturn(): Int {
    publicSplice { return it }
    return -1
}

class PublishedCallableVisibilityTests {
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
