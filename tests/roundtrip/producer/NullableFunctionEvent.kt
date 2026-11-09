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
