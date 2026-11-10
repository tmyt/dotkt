package roundtrip.enumevents

import kotlin.clr.ClrEvent
import kotlin.clr.ClrEventPolicy
import kotlin.clr.clrEvent
import kotlin.clr.ClrField
import kotlin.concurrent.Volatile
import System.ComponentModel.INotifyPropertyChanged
import System.ComponentModel.PropertyChangedEventArgs
import System.ComponentModel.PropertyChangedEventHandler

enum class SignalMode : INotifyPropertyChanged {
    A, B;
    val Changed: ClrEvent<System.Action> by clrEvent()
    override val PropertyChanged: ClrEvent<PropertyChangedEventHandler> by clrEvent()
    fun fire() { Changed(); PropertyChanged(this, PropertyChangedEventArgs(name)) }
}

enum class ForwardedMode : INotifyPropertyChanged by SignalMode.A { ONLY }

enum class EntryImplementedMode : INotifyPropertyChanged {
    A {
        override val PropertyChanged: ClrEvent<PropertyChangedEventHandler> by clrEvent()
        override fun fire() { PropertyChanged(this, PropertyChangedEventArgs(name)) }
    },
    B {
        override val PropertyChanged: ClrEvent<PropertyChangedEventHandler> by clrEvent()
        override fun fire() { PropertyChanged(this, PropertyChangedEventArgs(name)) }
    };
    open fun fire() { error("entry implementation must be selected") }
}

interface EntryEventHandle { val PropertyChanged: ClrEvent<PropertyChangedEventHandler> }
enum class EntryHandleMode : INotifyPropertyChanged, EntryEventHandle {
    A {
        override val PropertyChanged: ClrEvent<PropertyChangedEventHandler> by clrEvent()
        override fun fire() { PropertyChanged(this, PropertyChangedEventArgs(name)) }
    },
    B {
        override val PropertyChanged: ClrEvent<PropertyChangedEventHandler> by clrEvent()
        override fun fire() { PropertyChanged(this, PropertyChangedEventArgs(name)) }
    };
    open fun fire() { error("entry implementation must be selected") }
}

abstract class DeferredNativeEvents : INotifyPropertyChanged

class RecordingPolicy : ClrEventPolicy<System.Action>() {
    private val handlers = ArrayList<System.Action>()
    var adds = 0
    var removes = 0
    override fun add(handler: System.Action) { adds++; handlers.add(handler) }
    override fun remove(handler: System.Action) { removes++; handlers.remove(handler) }
    override fun invoke(vararg args: Any?) { for (handler in handlers.toList()) handler() }
}

enum class PolicyMode {
    A {
        private val EntryOnly: ClrEvent<System.Action> by clrEvent()
        override fun entryCheck(): Int {
            var value = 0
            var inheritedCalls = 0
            var hiddenCalls = 0
            val inherited = Changed.subscribe { inheritedCalls++ }
            val hidden = secretHandle().subscribe { hiddenCalls++ }
            Changed()
            fire()
            inherited.close()
            hidden.close()
            check(inheritedCalls == 2 && hiddenCalls == 1)
            val token = EntryOnly.subscribe { value += 42 }
            EntryOnly()
            token.close()
            EntryOnly()
            return value
        }
    }, B;
    var initializationOrder = ""
        private set
    private fun mark(value: String): String { initializationOrder += value; return value }
    private val before = mark("1")
    val policy = RecordingPolicy()
    private fun eventPolicy(): RecordingPolicy { mark("2"); return policy }
    val Changed: ClrEvent<System.Action> by clrEvent(eventPolicy())
    private val Secret: ClrEvent<System.Action> by clrEvent()
    init { mark("3") }
    private val after = mark("4")
    @ClrField val marker: Int = 7
    @Volatile var counter: Int = 0
    open fun entryCheck() = 0
    fun secretHandle() = Secret
    fun fire() { Changed(); Secret() }
}

enum class OverriddenMode {
    A {
        override val Changed: ClrEvent<System.Action> by clrEvent()
        override fun fire() { Changed() }
    }, B;
    open val Changed: ClrEvent<System.Action> by clrEvent()
    open fun fire() { Changed() }
}

fun localEnumEvents(): Int {
    var result = 0
    val token = PolicyMode.B.Changed.subscribe { result += 42 }
    PolicyMode.B.fire()
    token.close()
    PolicyMode.B.fire()
    return result
}
