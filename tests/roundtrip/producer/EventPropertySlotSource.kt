package roundtrip.eventpropertyslot

import kotlin.clr.ClrEvent
import kotlin.clr.ClrEventPolicy
import kotlin.clr.clrEvent
import EventPropertySlotInterop.IMixedEvent
import System.ComponentModel.PropertyChangedEventHandler
import System.ComponentModel.PropertyChangedEventArgs
import System.ComponentModel.INotifyPropertyChanged

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
    open val Changed: ClrEvent<System.Action1<T>> by clrEvent()
    fun fire(value: T) { Changed(value) }
}
open class InheritedPublisher : NativeBase<Int>(), HasChanged<Int>
class InheritedLeaf : InheritedPublisher()
open class LockedPublisher : HasChanged<Int> {
    final override val Changed: ClrEvent<System.Action1<Int>> by clrEvent()
}
class ForwardedPublisher<T>(source: HasChanged<T>) : HasChanged<T> by source
class OrdinaryHandle<T>(override val Changed: ClrEvent<System.Action1<T>>) : HasChanged<T>

interface HasFunctionChanged { val Changed: ClrEvent<(Int) -> Unit> }
class FunctionPublisher : HasFunctionChanged {
    override val Changed: ClrEvent<(Int) -> Unit> by clrEvent()
    fun fire(value: Int) { Changed(value) }
}

enum class EnumPublisher : HasChanged<Int> {
    A {
        override val Changed: ClrEvent<System.Action1<Int>> by clrEvent()
        override fun fire(value: Int) { Changed(value) }
    }, B;
    override open val Changed: ClrEvent<System.Action1<Int>> by clrEvent()
    open fun fire(value: Int) { Changed(value) }
}

class PcPublisher : IMixedEvent {
    override val PropertyChanged: ClrEvent<PropertyChangedEventHandler> by clrEvent()
    fun fire() { PropertyChanged(this, PropertyChangedEventArgs("value")) }
}

interface HasPc { val PropertyChanged: ClrEvent<PropertyChangedEventHandler> }
interface KotlinMixed : INotifyPropertyChanged, HasPc
class KotlinPcPublisher : KotlinMixed {
    override val PropertyChanged: ClrEvent<PropertyChangedEventHandler> by clrEvent()
    fun fire() { PropertyChanged(this, PropertyChangedEventArgs("value")) }
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
