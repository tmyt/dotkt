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
class LocalUnitFunctionSlots : FunctionBody<Unit>(), FunctionSlot<Unit>

fun <T> checkFunctionSlots(slot: FunctionSlot<T>, body: FunctionBody<T>, value: T) {
    check(slot.apply(value) { it } == value)
    check(body.apply(value) { it } == value)
    check(slot.extension(value) { this } == value)
    var called = false
    slot.consume(value) { check(it == value); called = true }
    check(called)
}
