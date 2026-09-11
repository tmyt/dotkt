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
open class MethodBody {
    @kotlin.clr.ClrName("Identity")
    fun <T> identity(value: T): T = value
}
open class UnitBody {
    @kotlin.clr.ClrName("ReadUnit")
    fun read() {}
}
