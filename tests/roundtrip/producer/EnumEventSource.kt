package roundtrip.enumevents

import kotlin.clr.ClrEvent
import kotlin.clr.ClrEventPolicy
import kotlin.clr.clrEvent
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
    open fun entryCheck() = 0
    fun secretHandle() = Secret
    fun fire() { Changed(); Secret() }
}

fun localEnumEvents(): Int {
    var result = 0
    val token = PolicyMode.B.Changed.subscribe { result += 42 }
    PolicyMode.B.fire()
    token.close()
    PolicyMode.B.fire()
    return result
}
