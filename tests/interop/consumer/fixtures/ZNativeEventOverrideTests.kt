import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import EventDelegation.NativeEventBase
import EventDelegation.AbstractEventMiddle
import EventDelegation.AbstractEventBase
import EventDelegation.IInheritedEventSource
import EventDelegation.IEventSource
import EventDelegation.IDefaultEvent
import EventDelegation.IGenericDefaultEvent
import EventDelegation.DefaultEventProbe
import kotlin.clr.ClrEvent
import kotlin.clr.clrEvent

private class NativeEventOverride : NativeEventBase<Int>() {
    override val Changed: ClrEvent<System.Action1<Int>> by clrEvent()
    fun fire(value: Int) { Changed(value) }
}

private class IndirectAbstractEvent : AbstractEventMiddle<Int>() {
    override val Changed by clrEvent()
    fun fire(value: Int) { Changed(value) }
}

private class InheritedInterfaceEvent : IInheritedEventSource<Int> {
    override val Changed by clrEvent()
    override fun Fire(value: Int) { Changed(value) }
}

private open class FinalEventBase {
    val Changed: ClrEvent<System.Action1<Int>> by clrEvent()
    fun fire(value: Int) { Changed(value) }
}

private class InheritedFinalEvent : FinalEventBase(), IEventSource<Int> {
    override fun Fire(value: Int) { fire(value) }
}

private class InheritedNativeDefaultEvent : IDefaultEvent
private class InheritedGenericDefaultEvent : IGenericDefaultEvent<String>

class NativeEventOverrideTests {
    @TestAttribute
    fun nativeEventsPreserveOverrideAndDefaultDispatch() {
        val native = NativeEventOverride()
        val nativeBase: NativeEventBase<Int> = native
        var value = 0
        val token = nativeBase.Changed.subscribe { value += it }
        native.fire(42)
        token.close()
        native.fire(100)
        assertEquals(42, value)
        assertEquals(0, native.BaseCount)

        val abstractSource = IndirectAbstractEvent()
        val abstractBase: AbstractEventBase<Int> = abstractSource
        val abstractToken = abstractBase.Changed.subscribe { value += it }
        abstractSource.fire(20)
        abstractToken.close()
        abstractSource.fire(100)
        assertEquals(62, value)

        val inherited = InheritedInterfaceEvent()
        val inheritedBase: IEventSource<Int> = inherited
        val interfaceToken = inheritedBase.Changed.subscribe { value += it }
        inherited.Fire(22)
        interfaceToken.close()
        assertEquals(84, value)

        val finalSource: IEventSource<Int> = InheritedFinalEvent()
        val finalToken = finalSource.Changed.subscribe { value += it }
        finalSource.Fire(16)
        finalToken.close()
        finalSource.Fire(100)
        assertEquals(100, value)

        DefaultEventProbe.Added = 0
        DefaultEventProbe.Removed = 0
        val defaultSource = InheritedNativeDefaultEvent()
        val defaultBase: IDefaultEvent = defaultSource
        var calls = 0
        val defaultToken = defaultBase.Changed.subscribe { sender, _ ->
            check(sender === defaultSource)
            calls++
        }
        check(calls == 1 && DefaultEventProbe.Added == 1)
        defaultToken.close()
        check(DefaultEventProbe.Removed == 1)
        val directDefaultToken = defaultSource.Changed.subscribe { _, _ -> calls++ }
        check(calls == 2 && DefaultEventProbe.Added == 2)
        directDefaultToken.close()
        check(DefaultEventProbe.Removed == 2)
        check(!DefaultEventProbe.DeclaresOwnEvent(defaultSource))

        DefaultEventProbe.GenericAdded = 0
        DefaultEventProbe.GenericRemoved = 0
        val genericSource = InheritedGenericDefaultEvent()
        val genericBase: IGenericDefaultEvent<String> = genericSource
        val genericToken = genericBase.Changed.subscribe { _ -> }
        check(DefaultEventProbe.GenericAdded == 1)
        genericToken.close()
        check(DefaultEventProbe.GenericRemoved == 1)
        val directGenericToken = genericSource.Changed.subscribe { _ -> }
        check(DefaultEventProbe.GenericAdded == 2)
        directGenericToken.close()
        check(DefaultEventProbe.GenericRemoved == 2)
        check(!DefaultEventProbe.DeclaresOwnEvent(genericSource))
    }
}
