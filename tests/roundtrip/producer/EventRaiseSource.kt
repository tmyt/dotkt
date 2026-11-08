package roundtrip.eventraise

import kotlin.clr.ClrEvent
import kotlin.clr.ClrEventPolicy
import kotlin.clr.clrEvent
import roundtrip.eventoverride.RecordingEventPolicy

open class Publisher<A, B> {
    val Changed: ClrEvent<System.Action1<B>> by clrEvent()
}

class PublisherOuter<A> {
    inner class NestedPublisher<B> {
        val Changed: ClrEvent<System.Action2<A, B>> by clrEvent()
    }
}

open class PolicyPublisher {
    val policy = RecordingEventPolicy()
    val Changed: ClrEvent<System.Action1<Int>> by clrEvent(policy)
}

private class LocalChild : Publisher<String, Int>() {
    fun fire(value: Int) { Changed(value) }
}

private class LocalPolicyChild : PolicyPublisher() {
    fun fire(value: Int) { Changed(value) }
}

class RaiseCountingPolicy : ClrEventPolicy<System.Action1<Int>>() {
    var total = 0
    override fun add(handler: System.Action1<Int>) {}
    override fun remove(handler: System.Action1<Int>) {}
    override fun invoke(vararg args: Any?) { total += args[0] as Int }
}

open class SuperPublisher {
    val basePolicy = RaiseCountingPolicy()
    open val Changed: ClrEvent<System.Action1<Int>> by clrEvent(basePolicy)
}

private class LocalSuperChild : SuperPublisher() {
    val policy = RaiseCountingPolicy()
    override val Changed: ClrEvent<System.Action1<Int>> by clrEvent(policy)
    fun fireBase(value: Int) { super.Changed(value) }
    fun later(value: Int): () -> Unit = { super.Changed(value) }
}

fun localInheritedRaises(): Int {
    val base = LocalSuperChild()
    base.fireBase(20)
    base.later(22)()
    check(base.basePolicy.total == 42 && base.policy.total == 0)
    val source = LocalChild()
    var result = 0
    val token = source.Changed.subscribe { result += it }
    source.fire(20)
    token.close()
    source.fire(100)
    val custom = LocalPolicyChild()
    val customToken = custom.Changed.subscribe { result += it }
    custom.fire(22)
    customToken.close()
    custom.fire(100)
    check(custom.policy.adds == 1 && custom.policy.removes == 1)
    return result
}
