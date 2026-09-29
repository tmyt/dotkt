import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.nullabletaskslots.*

private class ConsumerNullableTask(private val result: Int?) : NullableTaskSlot<Int?> {
    override suspend fun <U> read(marker: U, pause: suspend () -> Unit): Int? {
        pause()
        return result
    }
    override suspend fun <U> read(marker: U, text: String): Int? = result
}

private class ConsumerTaskBase(private val result: Long?) : NullableTaskBase<Long?>() {
    override suspend fun <U> read(marker: U, pause: suspend () -> Unit): Long? {
        pause()
        return result
    }
}

private class ConsumerInheritedTask(result: Long?) : ProducerTaskBody(result), NullableTaskSlot<Long?>

private interface ConsumerLocalTaskSlot<T> {
    suspend fun <U> read(marker: U, pause: suspend () -> Unit): T
}
private class ConsumerLocalInheritedTask(result: Long?) : ProducerTaskBody(result), ConsumerLocalTaskSlot<Long?>

private fun <T> checkNullableTask(expected: T, block: suspend (suspend () -> Unit) -> T) {
    val unfinished = Any()
    var actual: Any? = unfinished
    var pending: Continuation<Unit>? = null
    val action: suspend () -> T = { block { suspendCoroutine<Unit> { pending = it } } }
    action.startCoroutine(object : Continuation<T> {
        override val context: CoroutineContext = EmptyCoroutineContext
        override fun resumeWith(result: Result<T>) { actual = result.getOrThrow() }
    })
    check(actual === unfinished)
    pending!!.resume(Unit)
    check(actual == expected)
}

class NullableTaskSlotTests {
    @TestAttribute
    fun authoredNullableTaskSlotsPreserveDirectAndInterfaceCalls() {
        for (expected in listOf(47, null)) {
            val local = ProducerNullableTask(expected)
            val imported = ConsumerNullableTask(expected)
            for (slot in listOf<NullableTaskSlot<Int?>>(local, imported)) {
                checkNullableTask(expected) { pause ->
                    val value = slot.read("callback", pause)
                    check(value == expected)
                    slot.read(1, "ordinary overload")
                }
            }
            checkNullableTask(expected) { pause -> local.read(2, pause) }
            checkNullableTask(expected) { pause -> imported.read(3, pause) }
        }
    }

    @TestAttribute
    fun nullableTaskSlotsPreserveBaseAndInheritedCalls() {
        for (expected in listOf(53L, null)) {
            val concrete = ConsumerTaskBase(expected)
            val base: NullableTaskBase<Long?> = concrete
            checkNullableTask(expected) { pause -> base.read("base", pause) }
            checkNullableTask(expected) { pause -> concrete.read(5, pause) }
            val inherited = ConsumerInheritedTask(expected)
            val slot: NullableTaskSlot<Long?> = inherited
            checkNullableTask(expected) { pause -> slot.read("inherited", pause) }
            checkNullableTask(expected) { pause -> inherited.read(7, pause) }
            val localSlot: ConsumerLocalTaskSlot<Long?> = ConsumerLocalInheritedTask(expected)
            checkNullableTask(expected) { pause -> localSlot.read("local slot", pause) }
        }
    }
}
