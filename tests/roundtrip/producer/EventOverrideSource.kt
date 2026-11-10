package roundtrip.eventoverride

import kotlin.clr.ClrEvent
import kotlin.clr.ClrEventPolicy
import kotlin.clr.clrEvent

open class EventBase<A, B> {
    open val Changed: ClrEvent<System.Action1<B>> by clrEvent()
    fun fireFromBase(value: B) { Changed(value) }
}

open class EventMiddle<T> : EventBase<Int, T>()

class RecordingEventPolicy : ClrEventPolicy<System.Action1<Int>>() {
    private val handlers = ArrayList<System.Action1<Int>>()
    var adds = 0
    var removes = 0
    override fun add(handler: System.Action1<Int>) { adds++; handlers.add(handler) }
    override fun remove(handler: System.Action1<Int>) { removes++; handlers.remove(handler) }
    override fun invoke(vararg args: Any?) {
        for (handler in handlers.toList()) handler(args[0] as Int)
    }
}

open class PolicyEventBase {
    val basePolicy = RecordingEventPolicy()
    open val Changed: ClrEvent<System.Action1<Int>> by clrEvent(basePolicy)
    fun fireFromBase(value: Int) { Changed(value) }
}

class LocalPolicyEvent : PolicyEventBase() {
    val policy = RecordingEventPolicy()
    override val Changed: ClrEvent<System.Action1<Int>> by clrEvent(policy)
    fun fire(value: Int) { Changed(value) }
}

open class FunctionEventBase {
    open val Changed: ClrEvent<(Int) -> Unit> by clrEvent()
    fun fireFromBase(value: Int) { Changed(value) }
}

private class FunctionEventDerived : FunctionEventBase() {
    override val Changed by clrEvent()
    fun fire(value: Int) { Changed(value) }
}

private class WideFunctionEvent : FunctionEventBase() {
    override val Changed: ClrEvent<(Any) -> Unit> by clrEvent()
    fun fire(value: Int) { Changed(value) }
}

private open class ResultEventBase<R> {
    open val Requested: ClrEvent<() -> R> by clrEvent()
    fun fire() { Requested() }
}

private class UnitResultEvent : ResultEventBase<Unit>() {
    override val Requested: ClrEvent<() -> Unit> by clrEvent()
}

fun localPolicyDispatch(): Int {
    val functionSource = FunctionEventDerived()
    var functionValue = 0
    val functionToken = (functionSource as FunctionEventBase).Changed.subscribe { functionValue = it }
    functionSource.fireFromBase(42)
    functionSource.fire(42)
    functionToken.close()
    functionSource.fireFromBase(100)
    check(functionValue == 42)
    val wide = WideFunctionEvent()
    val wideToken = (wide as FunctionEventBase).Changed.subscribe { functionValue = it }
    wide.fireFromBase(43)
    wide.fire(43)
    wideToken.close()
    wide.fireFromBase(100)
    check(functionValue == 43)
    val result = UnitResultEvent()
    var calls = 0
    val resultToken = (result as ResultEventBase<Unit>).Requested.subscribe { calls++ }
    result.fire()
    resultToken.close()
    result.fire()
    check(calls == 1)
    val source = LocalPolicyEvent()
    var received = 0
    val token = (source as PolicyEventBase).Changed.subscribe { received += it }
    source.fire(20)
    source.fireFromBase(22)
    token.close()
    source.fire(100)
    check(source.basePolicy.adds == 0 && source.policy.adds == 1 && source.policy.removes == 1)
    return received
}
