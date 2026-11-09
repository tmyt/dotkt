package roundtriptests.eventpropertyslot

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.clr.ClrEvent
import kotlin.clr.clrEvent
import roundtrip.eventpropertyslot.*

private class DerivedPublisher : Publisher<String>() {
    override val Changed: ClrEvent<System.Action1<String>> by clrEvent()
    fun fireDerived(value: String) { Changed(value) }
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
    }
}
