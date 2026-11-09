package dotkt.support.events

import kotlin.clr.ClrEvent
import kotlin.clr.ClrEventPolicy
import kotlin.clr.clrEvent

class StaticIntEventPolicy : ClrEventPolicy<System.Action1<Int>>() {
    private var handler: System.Action1<Int>? = null
    var added: Any? = null
        private set
    var removed: Any? = null
        private set
    override fun add(handler: System.Action1<Int>) { added = handler; this.handler = handler }
    override fun remove(handler: System.Action1<Int>) { removed = handler; this.handler = null }
    override fun invoke(vararg args: Any?) { handler?.invoke(args[0] as Int) }
}

class NativeStaticSource {
    companion {
        val StaticPulse: ClrEvent<System.Action1<Int>> by clrEvent()
        val policy = StaticIntEventPolicy()
        val StaticPolicyPulse: ClrEvent<System.Action1<Int>> by clrEvent(policy)
    }
}

object StaticEventInitialization {
    var count = 0
    fun policy(): StaticIntEventPolicy { count++; return StaticIntEventPolicy() }
}

class GenericNativeStaticSource<T : Comparable<T>> {
    companion {
        val GenericPulse: ClrEvent<System.Action1<Int>> by clrEvent()
        val policy = StaticEventInitialization.policy()
        val GenericPolicyPulse: ClrEvent<System.Action1<Int>> by clrEvent(policy)
    }
}

fun exerciseLocalStaticEvents(): Int {
    var sum = 0
    val first = NativeStaticSource.StaticPulse.subscribe { sum += it }
    NativeStaticSource.StaticPulse(40)
    first.close()
    NativeStaticSource.StaticPulse(100)
    val second = NativeStaticSource.StaticPolicyPulse.subscribe { sum += it }
    NativeStaticSource.StaticPolicyPulse(2)
    second.close()
    NativeStaticSource.StaticPolicyPulse(100)
    check(NativeStaticSource.policy.added === NativeStaticSource.policy.removed)
    return sum
}
