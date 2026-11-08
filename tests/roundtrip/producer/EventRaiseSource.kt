package roundtrip.eventraise

import kotlin.clr.ClrEvent
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

fun localInheritedRaises(): Int {
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
