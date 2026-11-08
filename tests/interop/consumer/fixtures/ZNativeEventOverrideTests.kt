import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import EventDelegation.NativeEventBase
import kotlin.clr.ClrEvent
import kotlin.clr.clrEvent

private class NativeEventOverride : NativeEventBase<Int>() {
    override val Changed: ClrEvent<System.Action1<Int>> by clrEvent()
    fun fire(value: Int) { Changed(value) }
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
    }
}
