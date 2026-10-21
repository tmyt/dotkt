package roundtriptests.parameterslots

import NUnit.Framework.TestAttribute
import GenericValueInterop.NativeBoxApi
import GenericValueInterop.NativeBoxReceiver
import kotlin.clr.byref
import kotlin.coroutines.*
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

class SharedReceiver : KotlinBoxBase(), KotlinBoxReceiver, NativeBoxReceiver {
    override fun Accept(value: Box<String>): Any = value
}

private class LocalChoices<T> {
    fun choose(slot: Slot<String>): Int = 17
    fun choose(slot: Slot<Int>): Int = 29
}

private fun chooseLocal(owner: LocalChoices<*>, slot: Slot<Int>): Int = owner.choose(slot)
private fun chooseImported(owner: SlotChoices<*>, slot: Slot<Int>): Int = owner.choose(slot)
private suspend fun localSink(value: Box<String>): String = value.value
private suspend fun localForward(value: Box<String>): String = localSink(value)

class OrdinaryParameterSlotTests {
    @TestAttribute
    fun starOwnerOverloadsKeepBothSelectedDeclarations() {
        val integers = TrackedSlot(42)
        check(chooseLocal(LocalChoices<String>(), integers) == 29)
        check(chooseImported(SlotChoices<String>(), integers) == 29)
        val choices: SlotChoices<*> = SlotChoices<Int>()
        check(choices.choose(TrackedSlot("text")) == 17)
    }

    @TestAttribute
    fun sameModuleAndImportedSuspendParametersKeepColdSignatures() {
        var observed = ""
        val block: suspend () -> String = {
            localForward(Box("local")) + importedForward(Box("imported"))
        }
        block.startCoroutine(object : Continuation<String> {
            override val context: CoroutineContext get() = EmptyCoroutineContext
            override fun resumeWith(result: Result<String>) { observed = result.getOrThrow() }
        })
        check(observed == "localimported")
    }

    @TestAttribute
    fun typeVariableSlotsAndGeneratedOverridesStayImplemented() {
        val value = Box("generated")
        val handler: TypedHandler<Box<String>> = BoxHandler()
        check(handler.handle(value) === value)
        val anonymous: KotlinBoxReceiver = object : KotlinBoxReceiver {
            override fun Accept(value: Box<String>): Any = value
        }
        check(anonymous.Accept(value) === value)
        class Local : KotlinBoxReceiver { override fun Accept(value: Box<String>): Any = value }
        check((Local() as KotlinBoxReceiver).Accept(value) === value)
    }

    @TestAttribute
    fun nativeAndKotlinSlotsShareAnExactForwardingBridge() {
        val value = Box("shared")
        val receiver = SharedReceiver()
        check((receiver as KotlinBoxReceiver).Accept(value) === value) { "Kotlin interface dispatch" }
        check((receiver as KotlinBoxBase).Accept(value) === value) { "Kotlin base dispatch" }
        check(NativeBoxApi.Dispatch(receiver, value) === value) { "native interface dispatch" }
    }

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
        val property = PropertyOwner(value)
        property.box = location
        check(property.box === location)
    }
}
