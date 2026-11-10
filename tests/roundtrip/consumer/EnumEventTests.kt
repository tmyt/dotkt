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
import roundtrip.enumevents.EntryImplementedMode
import roundtrip.enumevents.EntryHandleMode
import roundtrip.enumevents.EntryEventHandle
import roundtrip.enumevents.DeferredNativeEvents
import System.ComponentModel.PropertyChangedEventHandler
import System.ComponentModel.PropertyChangedEventArgs
import kotlin.clr.clrEvent

private class ImportedDeferredNativeEvents : DeferredNativeEvents() {
    override val PropertyChanged: ClrEvent<PropertyChangedEventHandler> by clrEvent()
    fun fire() { PropertyChanged(this, PropertyChangedEventArgs("deferred")) }
}

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
        for (entry in EntryImplementedMode.entries) {
            val native: INotifyPropertyChanged = entry
            val other = if (entry === EntryImplementedMode.A) EntryImplementedMode.B else EntryImplementedMode.A
            var received = ""
            val token = native.PropertyChanged.subscribe { sender, args ->
                assertTrue(sender === entry)
                received += args.PropertyName
            }
            entry.fire()
            other.fire()
            token.close()
            entry.fire()
            assertEquals(entry.name, received)
        }
        assertTrue(EntryImplementedMode.valueOf("A") === EntryImplementedMode.A)
        assertTrue((EntryImplementedMode::class as System.Type).IsAbstract)
        assertTrue((EntryImplementedMode::class as System.Type).GetProperty("PropertyChanged") == null)
        for (entry in EntryHandleMode.entries) {
            val ordinary: EntryEventHandle = entry
            val native: INotifyPropertyChanged = entry
            var calls = 0
            val ordinaryToken = ordinary.PropertyChanged.subscribe { _, _ -> calls++ }
            val nativeToken = native.PropertyChanged.subscribe { _, _ -> calls++ }
            entry.fire()
            ordinaryToken.close()
            nativeToken.close()
            entry.fire()
            assertEquals(2, calls)
        }
        assertTrue((EntryHandleMode::class as System.Type).GetProperty("PropertyChanged")!!.GetGetMethod()!!.IsAbstract)
        val deferred = ImportedDeferredNativeEvents()
        var deferredCalls = 0
        val deferredToken = (deferred as INotifyPropertyChanged).PropertyChanged.subscribe { _, _ -> deferredCalls++ }
        deferred.fire()
        deferredToken.close()
        deferred.fire()
        assertEquals(1, deferredCalls)
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
