package roundtrip.unitcallbacks

interface Slot<T> { fun apply(value: T, callback: (T) -> T): T }
open class Body<T> {
    open fun apply(value: T, callback: (T) -> T): T = callback(value)
}
interface CallbackIdentitySlot<T> {
    fun remember(callback: (T) -> T): Any
    fun callback(): (T) -> T
}
class LocalUnitSlots : Body<Unit>(), Slot<Unit>
class LocalDirectUnit : Slot<Unit> {
    override fun apply(value: Unit, callback: (Unit) -> Unit): Unit = callback(value)
}
class LocalOverrideUnit : Body<Unit>(), Slot<Unit> {
    override fun apply(value: Unit, callback: (Unit) -> Unit): Unit = callback(value)
}
class LocalIdentity : CallbackIdentitySlot<Unit> {
    private var saved: ((Unit) -> Unit)? = null
    override fun remember(callback: (Unit) -> Unit): Any {
        saved = callback
        return callback
    }
    override fun callback(): (Unit) -> Unit = saved!!
    fun storedAsAny(): Any = saved!!
}
fun <T> throughSlot(slot: Slot<T>, value: T, callback: (T) -> T): T = slot.apply(value, callback)
fun <T> throughBody(body: Body<T>, value: T, callback: (T) -> T): T = body.apply(value, callback)
fun <T> rememberGeneric(slot: CallbackIdentitySlot<T>, callback: (T) -> T): Any = slot.remember(callback)
fun <T> returnedGeneric(slot: CallbackIdentitySlot<T>): (T) -> T = slot.callback()
fun <T> genericReturn(callback: (T) -> T): (T) -> T = callback
fun ordinary(action: () -> Unit) { action() }
fun ordinaryArgument(action: (Unit) -> Unit) { action(Unit) }

fun checkSameModuleUnitCallbacks() {
    var calls = 0
    val callback: (Unit) -> Unit = { calls++ }
    val inherited = LocalUnitSlots()
    check(throughSlot(inherited, Unit, callback) == Unit)
    check(throughBody(inherited, Unit, callback) == Unit)
    check(inherited.apply(Unit, callback) == Unit)
    val direct = LocalDirectUnit()
    throughSlot(direct, Unit, callback)
    direct.apply(Unit, callback)
    val overridden = LocalOverrideUnit()
    throughSlot(overridden, Unit, callback)
    throughBody(overridden, Unit, callback)
    overridden.apply(Unit, callback)
    val identity = LocalIdentity()
    val view: CallbackIdentitySlot<Unit> = identity
    check(identity.remember(callback) === callback)
    check(rememberGeneric(view, callback) === callback)
    check(identity.storedAsAny() === callback)
    check(identity.callback() === callback)
    check(view.callback() === callback)
    check(returnedGeneric(view) === callback)
    check(genericReturn(callback) === callback)
    identity.callback()(Unit)
    view.callback()(Unit)
    returnedGeneric(view)(Unit)
    genericReturn(callback)(Unit)
    ordinary { calls++ }
    ordinaryArgument(callback)
    check(calls == 14)
}
