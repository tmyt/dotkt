import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.nonnullbounds.*

private class ConsumerFactory : BoundFactory {
    override fun <U : Any> make(value: U): String = "factory"
}

private class ConsumerNullableBound : BoundOrdinarySlot<Int?> {
    override fun <U : Any> read(value: U): Int? = 43
}

private class ConsumerValueBound : BoundSlot<Int> {
    override fun <U : Any> read(value: U): Int = 43
    override suspend fun <U : Any> delayed(value: U, pause: suspend () -> Unit): Int {
        pause()
        return 47
    }
}

private class ConsumerBound : BoundBase<String>("base") {
    override fun <U : Any> read(value: U): String = "consumer"
    override suspend fun <U : Any> delayed(value: U, pause: suspend () -> Unit): String {
        pause()
        return "consumer"
    }
}

private fun <T> checkSuspendedBound(slot: BoundSlot<T>, expected: T) {
    var pending: Continuation<Unit>? = null
    var actual: Any? = "pending"
    val action: suspend () -> T = {
        slot.delayed(23) { suspendCoroutine<Unit> { pending = it } }
    }
    action.startCoroutine(object : Continuation<T> {
        override val context: CoroutineContext = EmptyCoroutineContext
        override fun resumeWith(result: Result<T>) { actual = result.getOrThrow() }
    })
    check(actual == "pending")
    pending!!.resume(Unit)
    check(actual == expected)
}

class NonNullBoundTests {
    @TestAttribute
    fun importedBoundsPreserveOrdinaryOverridesAndNullableControls() {
        val factory: BoundFactory = ConsumerFactory()
        check(factory.make(41) == "factory")
        val nullableSlot: BoundOrdinarySlot<Int?> = ConsumerNullableBound()
        check(nullableSlot.read("value") == 43)
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
        checkSuspendedBound(ConsumerValueBound(), 47)
        checkSuspendedBound(ConsumerBound(), "consumer")
        checkSuspendedBound(BoundBase("producer"), "producer")
    }
}
