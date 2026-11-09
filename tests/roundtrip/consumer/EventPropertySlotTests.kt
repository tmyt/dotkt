package roundtriptests.eventpropertyslot

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.clr.ClrEvent
import kotlin.clr.clrEvent
import roundtrip.eventpropertyslot.*
import EventPropertySlotInterop.IHasObject
import EventPropertySlotInterop.IHandleProperty
import System.ComponentModel.PropertyChangedEventHandler

private class DerivedPublisher : Publisher<String>() {
    override val Changed: ClrEvent<System.Action1<String>> by clrEvent()
    fun fireDerived(value: String) { Changed(value) }
}
private class ClrGetterPublisher : Publisher<Int>(), IHasObject
private class ClrInheritedGetterPublisher : InheritedPublisher(), IHasObject
private class InheritedRaiser : InheritedPublisher() { fun raise(value: Int) { Changed(value) } }
private class InheritedOverride : InheritedPublisher() {
    override val Changed: ClrEvent<System.Action1<Int>> by clrEvent()
    fun raise(value: Int) { Changed(value) }
}

class EventPropertySlotTests {
    @TestAttribute
    fun nativeEventsAlsoFillOrdinaryPropertySlotsAcrossDlls() {
        assertEquals(42, localPropertySlots())
        val publisher = Publisher<Int>()
        val contract: HasChanged<Int> = publisher
        val forwarded = ForwardedPublisher<Int>(publisher)
        val ordinary = OrdinaryHandle<Int>(publisher.Changed)
        var total = 0
        val directToken = publisher.Changed.subscribe { total += it }
        val contractToken = contract.Changed.subscribe { total += it }
        val forwardedToken = forwarded.Changed.subscribe { total += it }
        val ordinaryToken = ordinary.Changed.subscribe { total += it }
        publisher.fire(10)
        directToken.close(); contractToken.close(); forwardedToken.close(); ordinaryToken.close()
        publisher.fire(100)
        assertEquals(40, total)

        val policy = PolicyPublisher<Int>()
        val abstractSource: AbstractChanged<Int> = policy
        val policyContract: HasChanged<Int> = policy
        val first = abstractSource.Changed.subscribe { total += it }
        val second = policyContract.Changed.subscribe { total += it }
        policy.fire(1)
        first.close(); second.close(); policy.fire(100)
        assertEquals(42, total)

        val inherited = InheritedPublisher()
        val inheritedContract: HasChanged<Int> = inherited
        val inheritedToken = inheritedContract.Changed.subscribe { total += it }
        inherited.fire(8)
        inheritedToken.close()
        assertEquals(50, total)

        val raiser = InheritedRaiser()
        val raiseToken = raiser.Changed.subscribe { total += it }
        raiser.raise(1)
        raiseToken.close()
        val replaced = InheritedOverride()
        val replacedSource: HasChanged<Int> = replaced
        val replaceToken = replacedSource.Changed.subscribe { total += it }
        replaced.raise(1); replaced.fire(1)
        replaceToken.close()
        assertEquals(53, total)

        val clr = ClrGetterPublisher()
        val clrContract: IHasObject = clr
        val clrToken = (clrContract.Changed as ClrEvent<System.Action1<Int>>).subscribe { total += it }
        clr.fire(1)
        clrToken.close()
        assertEquals(54, total)
        val inheritedClr = ClrInheritedGetterPublisher()
        val inheritedClrContract: IHasObject = inheritedClr
        val inheritedClrToken = (inheritedClrContract.Changed as ClrEvent<System.Action1<Int>>).subscribe { total += it }
        inheritedClr.fire(1)
        inheritedClrToken.close()
        assertEquals(55, total)

        for (entry in EnumPublisher.entries) {
            val enumContract: HasChanged<Int> = entry
            val enumToken = enumContract.Changed.subscribe { total += it }
            entry.fire(1)
            enumToken.close(); entry.fire(100)
        }
        assertEquals(57, total)

        val pc = PcPublisher()
        val nativeForwarder = ForwardedNative(pc)
        val handleContract: IHandleProperty = nativeForwarder
        val nativeToken = nativeForwarder.PropertyChanged.subscribe { sender, _ -> assertTrue(sender === pc); total++ }
        val handleToken = (handleContract.PropertyChanged as ClrEvent<PropertyChangedEventHandler>).subscribe { _, _ -> total++ }
        pc.fire()
        nativeToken.close(); handleToken.close(); pc.fire()
        assertEquals(59, total)
        val localPc = KotlinPcPublisher()
        val localPcContract: HasPc = localPc
        val localPcToken = localPcContract.PropertyChanged.subscribe { _, _ -> total++ }
        localPc.fire()
        localPcToken.close()
        assertEquals(60, total)

        val derived = DerivedPublisher()
        val derivedContract: HasChanged<String> = derived
        var text = ""
        val derivedToken = derivedContract.Changed.subscribe { text += it }
        derived.fireDerived("a"); derived.fire("b")
        derivedToken.close(); derived.fireDerived("ignored")
        assertEquals("ab", text)

        val type = (publisher as System.Object).GetType()
        val event = type.GetEvent("Changed")!!
        assertTrue(event.GetAddMethod() != null && event.GetRemoveMethod() != null && event.GetRaiseMethod() != null)
        assertTrue(type.GetProperty("Changed")!!.GetGetMethod() != null)
        assertTrue((NativeBase<Int>() as System.Object).GetType().GetProperty("Changed") == null)
        assertTrue((ordinary as System.Object).GetType().GetEvent("Changed") == null)
        val locked = (LockedPublisher() as System.Object).GetType()
        assertTrue(locked.GetProperty("Changed")!!.GetGetMethod()!!.IsFinal)
        assertTrue(locked.GetEvent("Changed")!!.GetAddMethod()!!.IsFinal)
        val forwarderType = (nativeForwarder as System.Object).GetType()
        assertTrue(forwarderType.GetEvent("PropertyChanged") != null && forwarderType.GetProperty("PropertyChanged") != null)
        assertTrue((InheritedLeaf() as System.Object).GetType().GetProperty("Changed")!!.DeclaringType ==
            (inherited as System.Object).GetType())
    }
}
