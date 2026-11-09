package roundtrip.eventpropertyslot

import kotlin.clr.ClrEvent
import kotlin.clr.ClrEventPolicy
import kotlin.clr.clrEvent

interface HasChanged<T> { val Changed: ClrEvent<System.Action1<T>> }
abstract class AbstractChanged<T> { abstract val Changed: ClrEvent<System.Action1<T>> }

open class Publisher<T> : HasChanged<T> {
    override open val Changed: ClrEvent<System.Action1<T>> by clrEvent()
    fun fire(value: T) { Changed(value) }
}

class SlotPolicy<T> : ClrEventPolicy<System.Action1<T>>() {
    private val handlers = ArrayList<System.Action1<T>>()
    override fun add(handler: System.Action1<T>) { handlers.add(handler) }
    override fun remove(handler: System.Action1<T>) { handlers.remove(handler) }
    override fun invoke(vararg args: Any?) { for (handler in handlers.toList()) handler(args[0] as T) }
}

class PolicyPublisher<T> : AbstractChanged<T>(), HasChanged<T> {
    private val policy = SlotPolicy<T>()
    override val Changed: ClrEvent<System.Action1<T>> by clrEvent(policy)
    fun fire(value: T) { Changed(value) }
}

open class NativeBase<T> {
    val Changed: ClrEvent<System.Action1<T>> by clrEvent()
    fun fire(value: T) { Changed(value) }
}
class InheritedPublisher : NativeBase<Int>(), HasChanged<Int>
class ForwardedPublisher<T>(source: HasChanged<T>) : HasChanged<T> by source
class OrdinaryHandle<T>(override val Changed: ClrEvent<System.Action1<T>>) : HasChanged<T>

interface HasFunctionChanged { val Changed: ClrEvent<(Int) -> Unit> }
class FunctionPublisher : HasFunctionChanged {
    override val Changed: ClrEvent<(Int) -> Unit> by clrEvent()
    fun fire(value: Int) { Changed(value) }
}

fun localPropertySlots(): Int {
    val publisher = FunctionPublisher()
    val source: HasFunctionChanged = publisher
    var total = 0
    val token = source.Changed.subscribe { total += it }
    publisher.fire(42)
    token.close()
    publisher.fire(100)
    return total
}
