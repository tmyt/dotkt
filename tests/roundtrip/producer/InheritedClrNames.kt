package inheritedclrnames

interface StringSlot { fun read(): String }
interface ValueSlot<T> { fun read(): T }
open class StringBody {
    @kotlin.clr.ClrName("ReadText")
    fun read(): String = "producer"
}
class ProducedString : StringBody(), StringSlot, ValueSlot<String>
open class OwnerBody<T>(private val value: T) {
    @kotlin.clr.ClrName("ReadValue")
    fun read(): T = value
}
interface MethodSlot { fun <T> identity(value: T): T }
interface BoundSlot<T> { fun <R : T> identity(value: R): R }
interface BoundValue
class BoundPayload : BoundValue
open class BoundBody<T> {
    @kotlin.clr.ClrName("BoundIdentity")
    fun <R : T> identity(value: R): R = value
}
class ProducedBound : BoundBody<BoundValue>(), BoundSlot<BoundValue>
open class MethodBody {
    @kotlin.clr.ClrName("Identity")
    fun <T> identity(value: T): T = value
}
open class UnitBody {
    @kotlin.clr.ClrName("ReadUnit")
    fun read() {}
}
