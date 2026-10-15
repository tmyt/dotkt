import NUnit.Framework.TestAttribute
import roundtrip.unitcallbacks.*

private class UnitSlots : Body<Unit>(), Slot<Unit>
private class GenericUnitSlots<T> : Body<T>(), Slot<T>
private class DirectUnit : Slot<Unit> {
    override fun apply(value: Unit, callback: (Unit) -> Unit): Unit = callback(value)
}
private class NullableUnit : NullableSlot<Unit> {
    override fun apply(value: Unit, callback: ((Unit) -> Unit)?) { callback?.invoke(value) }
}
private class OverrideUnit : Body<Unit>(), Slot<Unit> {
    override fun apply(value: Unit, callback: (Unit) -> Unit): Unit = callback(value)
}
private class Identity : CallbackIdentitySlot<Unit> {
    private var saved: ((Unit) -> Unit)? = null
    override fun remember(callback: (Unit) -> Unit): Any {
        saved = callback
        return callback
    }
    override fun callback(): (Unit) -> Unit = saved!!
    fun storedAsAny(): Any = saved!!
}
private class WrappedIdentity : WrappedIdentitySlot<Unit> {
    private var saved: ((Unit) -> Unit)? = null
    private var savedList: List<(Unit) -> Unit>? = null
    private var savedArray: Array<(Unit) -> Unit>? = null
    override fun rememberNullable(callback: ((Unit) -> Unit)?): Any? { saved = callback; return saved }
    override fun rememberList(callbacks: List<(Unit) -> Unit>): Any { savedList = callbacks; return savedList!! }
    override fun rememberArray(callbacks: Array<(Unit) -> Unit>): Any { savedArray = callbacks; return savedArray!! }
}

class UnitCallbackSlotTests {
    @TestAttribute
    fun sameModuleUnitSlotsKeepInvocationAndFunctionIdentity() {
        checkSameModuleUnitCallbacks()
    }

    @TestAttribute
    fun importedUnitSlotsKeepInvocationAndFunctionIdentity() {
        var calls = 0
        val callback: (Unit) -> Unit = { calls++ }
        val inherited = UnitSlots()
        check(throughSlot(inherited, Unit, callback) == Unit)
        check(throughBody(inherited, Unit, callback) == Unit)
        check(inherited.apply(Unit, callback) == Unit)
        val direct = DirectUnit()
        throughSlot(direct, Unit, callback)
        direct.apply(Unit, callback)
        val overridden = OverrideUnit()
        throughSlot(overridden, Unit, callback)
        throughBody(overridden, Unit, callback)
        overridden.apply(Unit, callback)
        val generic = GenericUnitSlots<Unit>()
        throughSlot(generic, Unit, callback)
        throughBody(generic, Unit, callback)
        generic.apply(Unit, callback)
        val identity = Identity()
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
        check(calls == 17)
        val producerIdentity = LocalIdentity()
        check(rememberGeneric(producerIdentity, callback) === callback)
        check(producerIdentity.storedAsAny() === callback)
        check(producerIdentity.callback() === callback)
        check(returnedGeneric(producerIdentity) === callback)
        producerIdentity.callback()(Unit)
        returnedGeneric(producerIdentity)(Unit)
        check(calls == 19)
        val nullable = NullableUnit()
        nullable.apply(Unit, callback)
        throughNullable(nullable, Unit, callback)
        throughNullable(nullable, Unit, null)
        check(calls == 21)
        val wrapped = WrappedIdentity()
        val callbacks = listOf(callback)
        val array = arrayOf(callback)
        check(wrapped.rememberNullable(callback) === callback)
        check(wrapped.rememberList(callbacks) === callbacks)
        check(wrapped.rememberArray(array) === array)
        checkWrappedIdentity(wrapped, callback, callbacks, array)
    }
}
