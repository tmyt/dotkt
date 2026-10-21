package roundtriptests.parameterslots

import NUnit.Framework.TestAttribute
import GenericValueInterop.NativeBoxApi
import GenericValueInterop.NativeBoxReceiver
import kotlin.clr.byref
import roundtrip.genericvalueinterop.Box
import roundtrip.parameterslots.*
import roundtrip.uncheckedslot.*

private fun localUse(slot: Slot<Any>, owner: Any): Boolean = slot.allocate(owner)

class SlotUseImpl : SlotUse {
    override fun use(slot: Slot<Any>, owner: Any): Boolean = slot.allocate(owner)
}

class NativeReceiver : NativeBoxReceiver {
    override fun Accept(value: Box<String>): Any = value
}

class OrdinaryParameterSlotTests {
    @TestAttribute
    @Suppress("UNCHECKED_CAST")
    fun localAndImportedParametersKeepTheUncheckedIdentity() {
        val raw = rawSlot()
        val slot = raw as Slot<Any>
        val owner = ConcreteOwner()
        check(localUse(slot, owner))
        check(allocateSlot(slot, owner, raw))
        check(slot === raw && owner.allocations == 2)
    }

    @TestAttribute
    @Suppress("UNCHECKED_CAST")
    fun ownerAndMethodFramesPreserveValueReferenceAndNullableDispatch() {
        val integers = TrackedSlot(1)
        val widened = integers as Slot<Any>
        check(genericSlot(widened, 41, integers) == 1)
        check(integers.last == 41)
        check(SlotConsumer<Any>().use(widened, 43, integers) == 1)
        check(integers.last == 43)
        val nullable = TrackedSlot<String?>("before")
        check(genericSlot(nullable as Slot<Any?>, null, nullable) == 1)
        check(nullable.last == null)
        check(SlotConsumer<Any?>().use(nullable as Slot<Any?>, "after", nullable) == 1)
        check(nullable.last == "after")
    }

    @TestAttribute
    @Suppress("UNCHECKED_CAST")
    fun interfaceDispatchAndCollapsedOverloadsKeepSelectedDeclarations() {
        val raw = rawSlot()
        val owner = ConcreteOwner()
        val consumer: SlotUse = SlotUseImpl()
        check(consumer.use(raw as Slot<Any>, owner))
        check(owner.allocations == 1)
        check(choose(raw as Slot<String>) == 17)
        check(choose(raw as Slot<Int>) == 29)
    }

    @TestAttribute
    fun nativeCallsOverridesAndManagedLocationsRemainExact() {
        val value = Box("native")
        check(NativeBoxApi.Echo(value) === value)
        check(NativeBoxApi.Dispatch(NativeReceiver(), value) === value)
        var location = Box("before")
        takeRef(byref(location), value)
        check(location === value)
        check(NativeBoxApi.ReplaceAliased(byref(location), byref(location)))
        check(location.value == "second")
    }
}
