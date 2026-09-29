import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.nonnullbounds.*

private class ConsumerBound : BoundBase<String>("base") {
    override fun <U : Any> read(value: U): String = "consumer"
    override suspend fun <U : Any> delayed(value: U, pause: suspend () -> Unit): String {
        pause()
        return "consumer"
    }
}

private fun checkSuspendedBound(slot: BoundSlot<String>, expected: String) {
    var pending: Continuation<Unit>? = null
    var actual = "pending"
    val action: suspend () -> String = {
        slot.delayed(23) { suspendCoroutine<Unit> { pending = it } }
    }
    action.startCoroutine(object : Continuation<String> {
        override val context: CoroutineContext = EmptyCoroutineContext
        override fun resumeWith(result: Result<String>) { actual = result.getOrThrow() }
    })
    check(actual == "pending")
    pending!!.resume(Unit)
    check(actual == expected)
}

class NonNullBoundTests {
    @TestAttribute
    fun importedBoundsPreserveOrdinaryOverridesAndNullableControls() {
        val slot: BoundSlot<String> = ConsumerBound()
        check(slot.read(17) == "consumer" && slot.read("text") == "consumer")
        check(BoundBase(19).read("text") == 19)
        check(nonNull(29) == 29 && nonNull("text") == "text")
        check(nullable<String?>(null) == null && unconstrained<String?>(null) == null)
        check(NonNullBox(31).value == 31)
        check(NullableBox<String?>(null).value == null)
    }

    @TestAttribute
    fun importedBoundsPreserveSuspendOverridesAndResumption() {
        checkSuspendedBound(ConsumerBound(), "consumer")
        checkSuspendedBound(BoundBase("producer"), "producer")
    }
}
