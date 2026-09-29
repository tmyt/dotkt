import NUnit.Framework.TestAttribute
import kotlin.coroutines.*

private fun <T> importedExistentialSlot(owner: ExistentialSlotOwner<T>, raw: Any): Int {
    val item = raw as ExistentialSlotOwner<T>.Item
    check(owner.same(item, raw))
    return owner.read(item)
}
private fun <T> importedNullableExistentialSlot(owner: ExistentialSlotOwner<T>, raw: Any?): Int {
    val item = raw as ExistentialSlotOwner<T>.Item?
    return owner.nullable(item)
}

class ExistentialConstructedSlotTests {
    @TestAttribute
    fun constructedConsumersPreserveOwnerFramesAndIdentity() {
        val strings = ExistentialSlotOwner("owner")
        val integers = ExistentialSlotOwner(19)
        val stringItem = strings.Item(11)
        val intItem = integers.Item(23)
        check(strings.fromAny(stringItem) == 11)
        check(integers.fromAny(intItem) == 23)
        check(importedExistentialSlot(strings, stringItem) == 11)
        check(importedExistentialSlot(integers, intItem) == 23)
        check(importedNullableExistentialSlot(strings, stringItem) == 11)
        check(importedNullableExistentialSlot(integers, null) == 0)
        // No concrete slot consumes this cast result: retain its raw-classifier check.
        check(strings.unused(intItem) == 7)
        check(integers.unused(stringItem) == 7)
    }

    @TestAttribute
    fun suspendConsumersRetainTheConstructedInnerOwner() {
        val strings = ExistentialSlotOwner("owner")
        val integers = ExistentialSlotOwner(19)
        var completed = 0
        val action: suspend () -> Unit = {
            check(strings.suspended(strings.Item(11)) == 11)
            check(integers.suspended(integers.Item(23)) == 23)
        }
        action.startCoroutine(object : Continuation<Unit> {
            override val context: CoroutineContext = EmptyCoroutineContext
            override fun resumeWith(result: Result<Unit>) { result.getOrThrow(); completed++ }
        })
        check(completed == 1)
    }
}
