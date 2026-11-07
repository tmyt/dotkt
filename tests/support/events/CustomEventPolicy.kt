package dotkt.support.events

import kotlin.clr.ClrEvent
import kotlin.clr.ClrEventPolicy
import kotlin.clr.clrEvent
import System.ComponentModel.INotifyPropertyChanged
import System.ComponentModel.PropertyChangedEventArgs
import System.ComponentModel.PropertyChangedEventHandler

class RecordingEventPolicy<H>(private val dispatch: (H, Array<out Any?>) -> Unit) : ClrEventPolicy<H>() {
    private val handlers = ArrayList<H>()
    var adds = 0
        private set
    var removes = 0
        private set
    var added: Any? = null
        private set
    var removed: Any? = null
        private set

    override fun add(handler: H) { adds++; added = handler; handlers.add(handler) }
    override fun remove(handler: H) { removes++; removed = handler; handlers.remove(handler) }
    override fun invoke(vararg args: Any?) {
        for (handler in handlers.toList()) dispatch(handler, args)
    }
}

class CustomNotifyingSource : INotifyPropertyChanged {
    var initializationOrder = ""
        private set
    private fun mark(value: String): String { initializationOrder += value; return value }
    private val before = mark("1")
    val policy = RecordingEventPolicy<PropertyChangedEventHandler> { handler, args ->
        handler(args[0], args[1] as PropertyChangedEventArgs)
    }
    private fun eventPolicy(): RecordingEventPolicy<PropertyChangedEventHandler> {
        mark("2")
        return policy
    }
    override val PropertyChanged: ClrEvent<PropertyChangedEventHandler> by clrEvent(eventPolicy())
    init { mark("3") }
    private val after = mark("4")
    fun fire(name: String) { PropertyChanged(this, PropertyChangedEventArgs(name)) }
    // The same runtime delegate without the explicit event marker is an ordinary property.
    val ordinary by policy
}

class CustomGenericSource<A, B> {
    val policy = RecordingEventPolicy<(B) -> Unit> { handler, args ->
        @Suppress("UNCHECKED_CAST")
        handler(args[0] as B)
    }
    val Changed: ClrEvent<(B) -> Unit> by clrEvent(policy)
    fun fire(value: B) { Changed(value) }
    fun handle(): ClrEvent<(B) -> Unit> = Changed
}

object CustomResultSource {
    var result = 0
        private set
    val policy = RecordingEventPolicy<System.Func3<Int, String, Int>> { handler, args ->
        result = handler(args[0] as Int, args[1] as String)
    }
    val Requested: ClrEvent<System.Func3<Int, String, Int>> by clrEvent(policy)
    fun fire(number: Int, text: String) { Requested(number, text) }
}

class WeakActionPolicy : ClrEventPolicy<System.Action1<Any>>() {
    private val handlers = ArrayList<System.WeakReference>()
    val count: Int get() = handlers.size
    override fun add(handler: System.Action1<Any>) { handlers.add(System.WeakReference(handler)) }
    override fun remove(handler: System.Action1<Any>) {
        var i = 0
        while (i < handlers.size) {
            val target = handlers[i].Target
            if (target == null || target === handler) handlers.removeAt(i) else i++
        }
    }
    override fun invoke(vararg args: Any?) {
        val live = ArrayList<System.Action1<Any>>()
        var i = 0
        while (i < handlers.size) {
            val target = handlers[i].Target
            if (target == null) handlers.removeAt(i)
            else { live.add(target as System.Action1<Any>); i++ }
        }
        for (handler in live) handler(args[0]!!)
    }
}

class WeakPolicySource {
    val policy = WeakActionPolicy()
    val Changed: ClrEvent<System.Action1<Any>> by clrEvent(policy)
    fun fire(value: Any) { Changed(value) }
}
