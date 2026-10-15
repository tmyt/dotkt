package roundtrip.unitcallbacks

interface Slot<T> { fun apply(value: T, callback: (T) -> T): T }
interface NullableSlot<T> { fun apply(value: T, callback: ((T) -> T)?): T }
open class Body<T> {
    open fun apply(value: T, callback: (T) -> T): T = callback(value)
}
interface CallbackIdentitySlot<T> {
    fun remember(callback: (T) -> T): Any
    fun callback(): (T) -> T
}
interface WrappedIdentitySlot<T> {
    fun rememberNullable(callback: ((T) -> T)?): Any?
    fun rememberList(callbacks: List<(T) -> T>): Any
    fun rememberArray(callbacks: Array<(T) -> T>): Any
}
class LocalWrappedIdentity : WrappedIdentitySlot<Unit> {
    private var saved: ((Unit) -> Unit)? = null
    private var savedList: List<(Unit) -> Unit>? = null
    private var savedArray: Array<(Unit) -> Unit>? = null
    override fun rememberNullable(callback: ((Unit) -> Unit)?): Any? { saved = callback; return saved }
    override fun rememberList(callbacks: List<(Unit) -> Unit>): Any { savedList = callbacks; return savedList!! }
    override fun rememberArray(callbacks: Array<(Unit) -> Unit>): Any { savedArray = callbacks; return savedArray!! }
}
class LocalUnitSlots : Body<Unit>(), Slot<Unit>
class LocalDirectUnit : Slot<Unit> {
    override fun apply(value: Unit, callback: (Unit) -> Unit): Unit = callback(value)
}
class LocalNullableUnit : NullableSlot<Unit> {
    override fun apply(value: Unit, callback: ((Unit) -> Unit)?) { callback?.invoke(value) }
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
fun <T> throughNullable(slot: NullableSlot<T>, value: T, callback: ((T) -> T)?): T = slot.apply(value, callback)
fun <T> throughBody(body: Body<T>, value: T, callback: (T) -> T): T = body.apply(value, callback)
fun <T> rememberGeneric(slot: CallbackIdentitySlot<T>, callback: (T) -> T): Any = slot.remember(callback)
fun <T> returnedGeneric(slot: CallbackIdentitySlot<T>): (T) -> T = slot.callback()
fun <T> genericReturn(callback: (T) -> T): (T) -> T = callback
fun ordinary(action: () -> Unit) { action() }
fun ordinaryArgument(action: (Unit) -> Unit) { action(Unit) }

fun <T> checkWrappedIdentity(slot: WrappedIdentitySlot<T>, callback: (T) -> T,
                           callbacks: List<(T) -> T>, array: Array<(T) -> T>) {
    check(slot.rememberNullable(callback) === callback)
    check(slot.rememberNullable(null) == null)
    check(slot.rememberList(callbacks) === callbacks)
    check(slot.rememberArray(array) === array)
    check(callbacks[0] === callback && array[0] === callback)
}

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
    val nullable = LocalNullableUnit()
    nullable.apply(Unit, callback)
    throughNullable(nullable, Unit, callback)
    throughNullable(nullable, Unit, null)
    check(calls == 16)
    val wrapped = LocalWrappedIdentity()
    val callbacks = listOf(callback)
    val array = arrayOf(callback)
    check(wrapped.rememberNullable(callback) === callback)
    check(wrapped.rememberList(callbacks) === callbacks)
    check(wrapped.rememberArray(array) === array)
    checkWrappedIdentity(wrapped, callback, callbacks, array)
}
