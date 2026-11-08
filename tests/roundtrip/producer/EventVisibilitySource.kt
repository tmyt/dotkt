package roundtrip.eventvisibility

import kotlin.clr.ClrEvent
import kotlin.clr.ClrEventPolicy
import kotlin.clr.clrEvent

open class EventVisibilitySource<T> {
    private val Secret: ClrEvent<System.Action1<T>> by clrEvent()
    protected val Family: ClrEvent<System.Action1<T>> by clrEvent()
    internal val Assembly: ClrEvent<System.Action1<T>> by clrEvent()
    @PublishedApi internal val Published: ClrEvent<System.Action1<T>> by clrEvent()
    val Visible: ClrEvent<System.Action1<T>> by clrEvent()

    fun secretHandle(): ClrEvent<System.Action1<T>> = Secret
    fun assemblyHandle(): ClrEvent<System.Action1<T>> = Assembly
    fun publishedHandle(): ClrEvent<System.Action1<T>> = Published
    fun defaultHandle(event: ClrEvent<System.Action1<T>> = Secret): ClrEvent<System.Action1<T>> = event
    fun internalDefaultHandle(event: ClrEvent<System.Action1<T>> = Assembly): ClrEvent<System.Action1<T>> = event
    fun fire(value: T) {
        Secret(value); Family(value); Assembly(value); Published(value); Visible(value)
    }
}

class InheritedDefaultVisibilitySource : EventVisibilitySource<Int>() {
    fun defaultFamilyHandle(event: ClrEvent<System.Action1<Int>> = Family): ClrEvent<System.Action1<Int>> = event
}

fun readStarEvent(source: EventVisibilitySource<*>) { source.Visible }
fun <S : EventVisibilitySource<Int>> readBoundEvent(source: S) { source.Visible }

private class VisibilityPolicy : ClrEventPolicy<System.Action1<Int>>() {
    private var handler: System.Action1<Int>? = null
    override fun add(handler: System.Action1<Int>) { this.handler = handler }
    override fun remove(handler: System.Action1<Int>) { this.handler = null }
    override fun invoke(vararg args: Any?) { handler?.invoke(args[0] as Int) }
}

class PolicyVisibilitySource {
    private val Secret: ClrEvent<System.Action1<Int>> by clrEvent(VisibilityPolicy())
    fun handle(): ClrEvent<System.Action1<Int>> = Secret
    fun fire(value: Int) { Secret(value) }
}
