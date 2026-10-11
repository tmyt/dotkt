package roundtrip.functionslots

interface FunctionSlot<T> {
    fun apply(value: T, callback: (T) -> T): T
    fun consume(value: T, callback: (T) -> Unit)
    fun extension(value: T, callback: T.() -> T): T
}

open class FunctionBody<T> {
    open fun apply(value: T, callback: (T) -> T): T = callback(value)
    open fun consume(value: T, callback: (T) -> Unit) { callback(value) }
    open fun extension(value: T, callback: T.() -> T): T = value.callback()
}

class LocalFunctionSlots<T> : FunctionBody<T>(), FunctionSlot<T>

fun <T> checkFunctionSlots(slot: FunctionSlot<T>, body: FunctionBody<T>, value: T) {
    check(slot.apply(value) { it } == value)
    check(body.apply(value) { it } == value)
    check(slot.extension(value) { this } == value)
    var called = false
    slot.consume(value) { check(it == value); called = true }
    check(called)
}

interface GenericCallbackSlot<T> {
    fun complete(value: T, token: Any? = null): Any?
    fun <R : T> complete(value: R, token: Any?, callback: ((Throwable, R, Any) -> Unit)?): Any?
}

open class GenericCallbackBody<T> : GenericCallbackSlot<T> {
    override fun complete(value: T, token: Any?): Any? = token
    override fun <R : T> complete(value: R, token: Any?, callback: ((Throwable, R, Any) -> Unit)?): Any? {
        callback?.invoke(IllegalStateException("callback"), value, token ?: "none")
        return value
    }
}
