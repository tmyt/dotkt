package nullableunitreturns

fun optionalUnit(present: Boolean): Unit? = if (present) Unit else null
fun plainUnit() {}
fun <T> identity(value: T): T = value
interface Source { fun get(present: Boolean): Unit? }
open class Base : Source { override fun get(present: Boolean): Unit? = optionalUnit(present) }
fun callback(): (Boolean) -> Unit? = ::optionalUnit
fun action(): () -> Unit = ::plainUnit
fun optionalList(): List<Unit?> = listOf(Unit, null)
class Holder(var value: Unit?)
class CallbackHolder(var callback: (Boolean) -> Unit?)
fun invokeCallback(callback: (Boolean) -> Unit?, present: Boolean): Unit? = callback(present)
private fun plainCallback(present: Boolean) {}
fun unitCallback(): (Boolean) -> Unit = ::plainCallback
