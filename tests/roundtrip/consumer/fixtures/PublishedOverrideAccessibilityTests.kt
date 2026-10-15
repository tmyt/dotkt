import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.publishedoverrides.*

private class PublicOverrideConsumer : PublicDerived() {
    override fun hook(): Int = 14
}

private fun splicedOverrideReturn(value: Base<String>): Int {
    spliceHook(value) { return it }
    return -1
}

class PublishedOverrideAccessibilityTests {
    @TestAttribute
    fun ordinaryAndSplicedCallsKeepDerivedVirtualDispatch() {
        check(ordinaryHook(Derived<String>()) == 2)
        check(ordinaryHook(Leaf()) == 3)
        check(splicedOverrideReturn(Derived<String>()) == 23)
        check(splicedOverrideReturn(Leaf()) == 25)
        check(ordinaryHook(PublicOverrideConsumer()) == 14)
        check(ordinaryHook(anonymousOverride()) == 15)
        check(ordinaryHook(localOverride()) == 16)
        check(ordinaryInternalControl(Derived<String>()) == 8)
    }

    @TestAttribute
    fun propertyAndConstructedGenericOverridesKeepTheirSlots() {
        check(spliceSelect(Derived<Int>(), 1, 2) { it } == 2)
        check(spliceSelect(Leaf(), "first", "second") { it } == "second!")
        check(spliceProperties(Derived<String>()) { it } == 17)
        val leaf = Leaf()
        check(spliceProperties(leaf) { it } == 20)
        setNewAccessor(leaf)
        check(spliceProperties(leaf) { it } == 29)
    }

    @TestAttribute
    fun abstractAndSuspendOverridesKeepTheirSlots() {
        check(spliceAbstract(Concrete()) { it } == 10)
        var value = 0
        var complete = false
        val operation: suspend () -> Int = { callSuspended(Derived<String>()) }
        operation.startCoroutine(object : Continuation<Int> {
            override val context: CoroutineContext get() = EmptyCoroutineContext
            override fun resumeWith(result: Result<Int>) {
                value = result.getOrThrow()
                complete = true
            }
        })
        check(complete && value == 12)
    }
}
