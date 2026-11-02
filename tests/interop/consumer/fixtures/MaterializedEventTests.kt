import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import MaterializedEvents.Source
import MaterializedEvents.Derived
import MaterializedEvents.ExplicitSource
import MaterializedEvents.ISource
import MaterializedEvents.Concurrency
import Eventext.Beacon
import dotkt.support.events.EventBox
import dotkt.support.events.LocalSource
import dotkt.support.events.NominalLocalSource
import dotkt.support.events.StoredEvent
import dotkt.support.events.ForwardedEvent
import dotkt.support.events.InheritedForwardedEvent
import dotkt.support.events.GenericSource
import dotkt.support.events.ReorderedSource
import dotkt.support.events.passEvent
import dotkt.support.events.subscribeLater

class MaterializedEventTests {
    @TestAttribute
    fun retainedReceiverIsEvaluatedOnceAndCanOutliveTheRead() {
        val source = Source()
        var reads = 0
        fun next(): Source { reads++; return source }
        val event = next().Changed
        assertEquals(1, reads)
        assertEquals(0, source.AddCount)
        var value = 0
        val subscription = event.subscribe { value = it }
        source.Fire(42)
        assertEquals(42, value)
        subscription.close()
        subscription.close()
        source.Fire(100)
        assertEquals(42, value)
        assertEquals(1, reads)
        assertEquals(1, source.RemoveCount)
        assertTrue(source.AddedHandler === source.RemovedHandler)
    }

    @TestAttribute
    fun fieldsPropertiesReturnsAndCapturesComposeAcrossDlls() {
        val source = Source()
        val other = Source()
        val box = EventBox(passEvent(source.Changed))
        var sum = 0
        val later = box.event.subscribeLater(System.Action1<Int> { sum += it })
        box.event = other.Changed
        val subscription = later()
        source.Fire(6)
        other.Fire(99)
        assertEquals(6, sum)
        subscription.close()
        source.Fire(10)
        assertEquals(6, sum)
    }

    @TestAttribute
    fun staticVirtualAndExplicitInterfaceHandlesKeepTheirDispatch() {
        val staticEvent = passEvent(Beacon.Pinged)
        var value = 0
        val staticSubscription = staticEvent.subscribe { value += it }
        Beacon.Ping(3)
        staticSubscription.close()
        Beacon.Ping(99)
        val derived = Derived()
        val base: Source = derived
        val virtualEvent = base.Changed
        val virtualSubscription = virtualEvent.subscribe { value += it }
        derived.Fire(7)
        assertEquals(1, derived.VirtualAdds)
        virtualSubscription.close()
        derived.Fire(99)
        val explicit = ExplicitSource()
        val face: ISource = explicit
        val interfaceEvent = face.Changed
        val interfaceSubscription = interfaceEvent.subscribe { value += it }
        explicit.Fire(11)
        interfaceSubscription.close()
        explicit.Fire(99)
        assertEquals(21, value)
    }

    @TestAttribute
    fun handlerShapeIsLosslessIncludingZeroArgumentsAndNonUnitReturn() {
        val source = Source()
        var calls = 0
        var sender: Any? = null
        var payload = ""
        val empty = passEvent(source.Empty).subscribe { calls++ }
        val pair = passEvent(source.Pair).subscribe { first, second -> sender = first; payload = second }
        val request = passEvent(source.Requested).subscribe { first, second, third, fourth ->
            first + second.length + (if (third) 10 else 0) + fourth.toInt()
        }
        source.FireEmpty()
        source.FirePair(source, "payload")
        assertEquals(1, calls)
        assertTrue(sender === source)
        assertEquals("payload", payload)
        assertEquals(42, source.Request(20, "abc", true, 9L))
        empty.close(); pair.close(); request.close()
        assertEquals(-1, source.Request(20, "abc", true, 9L))
    }

    @TestAttribute
    fun concurrentCloseRemovesTheExactHandlerOnce() {
        val source = Source()
        val event = source.Changed
        val subscription = event.subscribe { }
        Concurrency.DeliverAndClose(source) { subscription.close() }
        Concurrency.CloseMany { subscription.close() }
        assertEquals(1, source.RemoveCount)
        assertTrue(source.AddedHandler === source.RemovedHandler)
    }

    @TestAttribute
    fun kotlinDeclaredEventAndOrdinaryHandlePropertySurviveReimport() {
        val source = LocalSource()
        val event = EventBox(source.handle()).event
        var sum = 0
        val subscription = event.subscribe { sum += it }
        source.fire(42)
        subscription.close()
        source.fire(99)
        assertEquals(42, sum)
    }

    @TestAttribute
    fun retainedGenericSourcePreservesReorderedOwnerAndHandlerFrames() {
        val source = GenericSource<String, Int>()
        val wrapper = ReorderedSource<Int, String>(source)
        var value = 0
        val subscription = wrapper.handle().subscribe { value = it }
        source.fire(42)
        subscription.close()
        source.fire(99)
        assertEquals(42, value)
    }

    @TestAttribute
    fun ordinaryHandlePropertiesKeepTheirRoleThroughClassDelegationAndInheritance() {
        val source = Source()
        val stored = StoredEvent(source.Changed)
        val direct = ForwardedEvent(stored)
        val inherited = InheritedForwardedEvent(stored)
        var sum = 0
        val first = direct.event.subscribe { sum += it }
        val second = inherited.event.subscribe { sum += it * 10 }
        source.Fire(2)
        assertEquals(22, sum)
        first.close()
        source.Fire(3)
        assertEquals(52, sum)
        second.close()
        source.Fire(99)
        assertEquals(52, sum)
        assertEquals(2, source.RemoveCount)
    }

    @TestAttribute
    fun reentrantCloseAndThrowingRemovalDoNotRemoveTwice() {
        val source = Source()
        val subscription = source.Changed.subscribe { }
        var removalCallbacks = 0
        source.Removing = System.Action { removalCallbacks++; subscription.close() }
        source.ThrowOnRemove = true
        var threw = false
        try { subscription.close() } catch (_: System.InvalidOperationException) { threw = true }
        assertTrue(threw)
        subscription.close()
        assertEquals(1, source.RemoveCount)
        assertEquals(1, removalCallbacks)
        assertTrue(source.AddedHandler === source.RemovedHandler)
    }

    @TestAttribute
    fun kotlinDeclaredNominalEventCanBeReadDirectlyAcrossDlls() {
        val source = NominalLocalSource()
        val event = source.Changed
        var value = 0
        val subscription = event.subscribe { value = it }
        source.fire(42)
        subscription.close()
        source.fire(99)
        assertEquals(42, value)
    }
}
