package roundtrip.nullablefunctionevent

import kotlin.clr.ClrEvent
import kotlin.clr.clrEvent

class NullableFunctionEvent<T> {
    val Changed: ClrEvent<((T) -> Unit)?> by clrEvent()
    fun fire(value: T) { Changed(value) }
}

fun localNullableFunctionEvent(): Int {
    val source = NullableFunctionEvent<Int>()
    val absent: ((Int) -> Unit)? = null
    val emptyToken = source.Changed.subscribe(absent)
    source.fire(100)
    emptyToken.close()
    var total = 0
    val handler: ((Int) -> Unit)? = { total += it }
    val token = source.Changed.subscribe(handler)
    source.fire(20)
    source.fire(22)
    token.close()
    source.fire(100)
    return total
}

class ReceiverFunctionEvent<T> {
    val Changed: ClrEvent<T.(Int) -> Unit> by clrEvent()
    fun fire(receiver: T, value: Int) { Changed(receiver, value) }
}

fun localReceiverFunctionEvent(): Int {
    val source = ReceiverFunctionEvent<Int>()
    source.fire(1, 1)
    var total = 0
    val handler: Int.(Int) -> Unit = { value -> total += this * value }
    val first = source.Changed.subscribe(handler)
    val second = source.Changed.subscribe(handler)
    source.fire(2, 3)
    first.close()
    source.fire(3, 10)
    second.close()
    source.fire(100, 100)
    return total
}
