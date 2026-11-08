import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import EventDelegation.NativeEventBase
import EventDelegation.AbstractEventMiddle
import EventDelegation.AbstractEventBase
import EventDelegation.IInheritedEventSource
import EventDelegation.IEventSource
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

class NativeEventOverrideTests {
    @TestAttribute
    fun classOverrideUsesNativeDelegateWithoutBaseRaiseSlot() {
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
    }
}
