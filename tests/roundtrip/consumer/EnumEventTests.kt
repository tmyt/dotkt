package roundtriptests.enumevents

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.ComponentModel.INotifyPropertyChanged
import System.Reflection.BindingFlags
import kotlin.clr.ClrEvent
import roundtrip.enumevents.SignalMode
import roundtrip.enumevents.PolicyMode
import roundtrip.enumevents.ForwardedMode
import roundtrip.enumevents.OverriddenMode
import roundtrip.enumevents.localEnumEvents

private fun exercise(event: ClrEvent<System.Action>, fire: () -> Unit, other: () -> Unit): Int {
    var calls = 0
    val token = event.subscribe { calls++ }
    fire()
    other()
    token.close()
    fire()
    return calls
}

class EnumEventTests {
    @TestAttribute
    fun enumBodyEventsPreserveEntryStorageAndNativeSlotsAcrossDlls() {
        assertEquals(42, localEnumEvents())
        assertEquals(1, exercise(SignalMode.A.Changed, { SignalMode.A.fire() }, { SignalMode.B.fire() }))
        val beforeAdds = PolicyMode.A.policy.adds
        val beforeRemoves = PolicyMode.A.policy.removes
        assertEquals(1, exercise(PolicyMode.A.Changed, { PolicyMode.A.fire() }, { PolicyMode.B.fire() }))
        assertEquals(beforeAdds + 1, PolicyMode.A.policy.adds)
        assertEquals(beforeRemoves + 1, PolicyMode.A.policy.removes)
        assertEquals(1, exercise(PolicyMode.A.secretHandle(), { PolicyMode.A.fire() }, { PolicyMode.B.fire() }))
        assertEquals("1234", PolicyMode.A.initializationOrder)
        assertEquals("1234", PolicyMode.B.initializationOrder)
        assertEquals(42, PolicyMode.A.entryCheck())
        assertEquals(0, PolicyMode.B.entryCheck())
        assertEquals(1, exercise(OverriddenMode.A.Changed, { OverriddenMode.A.fire() }, { OverriddenMode.B.fire() }))
        assertEquals(7, PolicyMode.A.marker)
        PolicyMode.A.counter = 12
        assertEquals(12, PolicyMode.A.counter)
        assertTrue(PolicyMode.valueOf("A") === PolicyMode.A && PolicyMode.entries[1] === PolicyMode.B)

        val native: INotifyPropertyChanged = SignalMode.A
        var property = ""
        val token = native.PropertyChanged.subscribe { sender, args ->
            assertTrue(sender === SignalMode.A)
            property += args.PropertyName
        }
        var forwardedCalls = 0
        val forwarded = ForwardedMode.ONLY.PropertyChanged.subscribe { _, _ -> forwardedCalls++ }
        SignalMode.A.fire()
        token.close()
        forwarded.close()
        SignalMode.A.fire()
        assertEquals("A", property)
        assertEquals(1, forwardedCalls)

        for (type in arrayOf(SignalMode::class as System.Type, PolicyMode::class as System.Type)) {
            val event = type.GetEvent("Changed")!!
            assertEquals("System.Action", event.EventHandlerType!!.FullName)
            assertTrue(event.GetAddMethod()!!.IsPublic && event.GetRemoveMethod()!!.IsPublic)
            assertTrue(event.GetRaiseMethod() != null && type.GetProperty("Changed") == null)
        }
        val type = PolicyMode::class as System.Type
        val secret = type.GetEvent("Secret", BindingFlags.Instance or BindingFlags.NonPublic)!!
        assertTrue(secret.GetAddMethod(true)!!.IsPrivate && type.GetEvent("Secret") == null)
        assertTrue(type.GetField("marker")!!.IsPublic && type.GetProperty("marker") == null)
        assertTrue(type.GetFields(BindingFlags.Instance or BindingFlags.NonPublic).any { field ->
            field.IsPrivate && field.GetRequiredCustomModifiers().any { it.FullName == "System.Runtime.CompilerServices.IsVolatile" }
        })
    }
}
