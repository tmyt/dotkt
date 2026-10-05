import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import GenericValueInterop.ExistentialOwnerApi
import GenericValueInterop.ExistentialOwnerStorage
import GenericValueInterop.ExistentialOwnerOverride
import GenericValueInterop.ExistentialNativeReader

private class ExistentialReader<T>(private val value: T) : ExistentialNativeReader<T> {
    override fun Read(): T = value
}
private class ExistentialReaderHolder(val reader: ExistentialNativeReader<String>)
private fun <T> readerView(value: ExistentialReader<T>): ExistentialNativeReader<T> = value
private fun <T> selectReader(first: ExistentialReader<T>, second: ExistentialReader<T>, takeFirst: Boolean):
    ExistentialNativeReader<T> = if (takeFirst) first else second

private class ExistentialNativeOverride : ExistentialOwnerOverride() {
    override fun Echo(value: ExistentialSlotOwner<String>): ExistentialSlotOwner<String> = value
}

private fun <T> readCapturedItem(item: ExistentialSlotOwner<T>.Item): Int = item.count

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
    fun nativeBoundariesRetainExactConstructedSlots() {
        val reader = ExistentialReader("value")
        val native: ExistentialNativeReader<String> = reader
        check(native.Read() == "value")
        check(readerView(reader) === reader)
        check(ExistentialReaderHolder(reader).reader === reader)
        check(ExistentialOwnerApi.Read(reader) == "value")
        val second = ExistentialReader("second")
        check(selectReader(reader, second, true) === reader)
        check(selectReader(reader, second, false) === second)
        check(ExistentialOwnerApi.Read(ExistentialReader(23)) == 23)
        val owner = ExistentialSlotOwner("kotlin")
        check(ExistentialOwnerApi.Echo(owner) === owner)
        val imported = ExistentialOwnerApi.Make()
        check(imported.value == "native")
        check(ExistentialOwnerApi.Echo(imported) === imported)
        val storage = ExistentialOwnerStorage(owner)
        check(storage.Slot === owner)
        storage.Slot = imported
        check(storage.Slot === imported)
        storage.Value = owner
        check(storage.Value === owner)
        check(ExistentialOwnerApi.Invoke({ it }, owner) === owner)
        check(ExistentialOwnerApi.Dispatch(ExistentialNativeOverride(), owner) === owner)
        check(ExistentialNativeOverride().Echo("untouched") == "base:untouched")
    }

    @TestAttribute
    fun constructedConsumersPreserveOwnerFramesAndIdentity() {
        val strings = ExistentialSlotOwner("owner")
        val integers = ExistentialSlotOwner(19)
        val stringItem = strings.Item(11)
        val intItem = integers.Item(23)
        check(ExistentialOwnerHolder(strings).owner === strings)
        check(ExistentialItemHolder(intItem).item === intItem)
        check(strings.fromAny(stringItem) == 11)
        check(strings.inlineRead(stringItem) == 28)
        check(strings.readPrivate(strings, stringItem) == 11)
        check(strings.readPrivate(strings, intItem) == 23)
        check(integers.fromAny(intItem) == 23)
        check(importedExistentialSlot(strings, stringItem) == 11)
        check(importedExistentialSlot(integers, intItem) == 23)
        check(importedNullableExistentialSlot(strings, stringItem) == 11)
        check(importedNullableExistentialSlot(integers, null) == 0)
        // No concrete slot consumes this cast result: retain its raw-classifier check.
        check(strings.unused(intItem) == 7)
        check(integers.unused(stringItem) == 7)
        check(strings.fromAny(intItem) == 23)
        check(integers.fromAny(stringItem) == 11)
        check(importedExistentialSlot(strings, intItem) == 23)
        check(importedNullableExistentialSlot(integers, stringItem) == 11)
        strings.store(intItem)
        check(strings.storedItem() === intItem)
        check(strings.storedItem()!!.count == 23)
    }

    @TestAttribute
    fun capturedStarArgumentsDoNotRequireAnObjectConstruction() {
        val strings = ExistentialSlotOwner("owner")
        val item: ExistentialSlotOwner<*>.Item = strings.Item(23)
        check(readCapturedItem(item) == 23)
    }

    @TestAttribute
    fun inheritedCarrierArgumentsPreserveTheSelectedBaseSlot() {
        val owner = ExistentialHierarchyOwner<String>()
        check(owner.fromAny(owner.Derived()) == 23)
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
        var pending: Continuation<Unit>? = null
        val crossedItem = integers.Item(29)
        val delayed: suspend () -> Unit = {
            check(strings.deferred(crossedItem) {
                suspendCoroutine<Unit> { pending = it }
            } == 29)
        }
        delayed.startCoroutine(object : Continuation<Unit> {
            override val context: CoroutineContext = EmptyCoroutineContext
            override fun resumeWith(result: Result<Unit>) { result.getOrThrow(); completed++ }
        })
        check(completed == 1)
        check(pending != null)
        pending.resume(Unit)
        check(completed == 2)
    }
}
