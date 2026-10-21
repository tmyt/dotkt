package roundtrip.parameterslots

import kotlin.clr.ClrRef
import roundtrip.genericvalueinterop.Box
import roundtrip.uncheckedslot.Slot

fun allocateSlot(slot: Slot<Any>, owner: Any, original: Any): Boolean {
    check(slot === original)
    return slot.allocate(owner)
}

fun <F> genericSlot(slot: Slot<F>, owner: F, original: Any): Int {
    check(slot === original)
    check(slot.allocate(owner))
    return slot.free(owner)
}

class SlotConsumer<F> {
    fun use(slot: Slot<F>, owner: F, original: Any): Int = genericSlot(slot, owner, original)
}

class TrackedSlot<F>(var last: F) : Slot<F>() {
    override fun allocate(owner: F): Boolean { last = owner; return true }
    override fun free(owner: F): Int = if (last == owner) 1 else 0
}

interface SlotUse { fun use(slot: Slot<Any>, owner: Any): Boolean }

class SlotChoices<T> {
    fun choose(slot: Slot<String>): Int = 17
    fun choose(slot: Slot<Int>): Int = 29
}

interface TypedHandler<T> { fun handle(value: T): Any }
class BoxHandler : TypedHandler<Box<String>> {
    override fun handle(value: Box<String>): Any = value
}

interface KotlinBoxReceiver { fun Accept(value: Box<String>): Any }
open class KotlinBoxBase { open fun Accept(value: Box<String>): Any = "base" }

suspend fun importedSink(value: Box<String>): String = value.value
suspend fun importedForward(value: Box<String>): String = importedSink(value)

class PropertyOwner(var box: Box<String>)

fun choose(slot: Slot<String>): Int = 17
fun choose(slot: Slot<Int>): Int = 29

fun takeRef(slot: ClrRef<Box<String>>, replacement: Box<String>) {
    slot.value = replacement
}
