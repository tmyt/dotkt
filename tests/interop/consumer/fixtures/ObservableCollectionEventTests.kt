// .NET event-consumption battery, using events projected through reference KLIBs.
// Deterministic per-handler counters assert exact synchronous fire multiplicities.
//
// These CONSUME .NET events (`subscribe` on an ObservableCollection<T>); they are NOT ClrEventTests, which is about
// a Kotlin class IMPLEMENTING a .NET interface event. bir2cir's ClrEventSubscriptionBinding binds subscribe to the
// event's add/remove accessors; ObservableCollection.Add raises the event SYNCHRONOUSLY on the calling thread, so the
// handler fires deterministically with no UI loop.
//
// Coverage preserved (old case -> method):
//   il-event      -> instanceEventSubscribeClose   .NET INSTANCE event ObservableCollection.CollectionChanged
//   il-ifaceevent -> interfaceEventSubscribeClose  INTERFACE .NET event INotifyPropertyChanged.PropertyChanged via ObservableCollection's explicit impl
//
// Helpers are private members of the feature-named fixture, so no top-level collision prefix is needed.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.Collections.ObjectModel.ObservableCollection
import System.ComponentModel.INotifyPropertyChanged

class ObservableCollectionEventTests {
    private fun <T> subscribeGeneric(
        collection: ObservableCollection<T>,
        handler: (Any?, Any?) -> Unit,
    ): AutoCloseable = collection.CollectionChanged.subscribe(handler)

    // il-event: subscriptions retain the exact direct-lambda/stored handler and close removes each one.
    @TestAttribute
    fun instanceEventSubscribeClose() {
        val c = ObservableCollection<Int>()
        var changed = 0
        val direct = c.CollectionChanged.subscribe { _, _ -> changed++ }
        c.Add(10)                                     // -> "changed"
        c.Add(20)                                     // -> "changed"
        assertEquals(2, changed)                      // literal fired twice
        assertEquals(2, c.Count)                      // 2

        var hFired = 0
        val h: (Any?, Any?) -> Unit = { _, _ -> hFired++ }
        val stored = c.CollectionChanged.subscribe(h)
        c.Add(30)                                     // literal + h both fire -> "changed", "h fired"
        assertEquals(3, changed)                      // literal fired again
        assertEquals(1, hFired)                        // h fired once
        stored.close()
        c.Add(40)                                     // only the literal fires -> "changed"
        assertEquals(4, changed)                       // literal fired again
        assertEquals(1, hFired)                        // h did NOT fire after close
        assertEquals(4, c.Count)                       // 4
        direct.close()
    }

    // il-ifaceevent: subscribe/close on an INTERFACE-typed receiver (explicit interface impl).
    // ObservableCollection<T> implements INotifyPropertyChanged explicitly; the interface-typed view exposes
    // PropertyChanged, and Add() raises it (a callvirt on the interface slot).
    @TestAttribute
    fun interfaceEventSubscribeClose() {
        val c = ObservableCollection<Int>()
        val n: INotifyPropertyChanged = c             // interface-typed receiver (explicit interface implementation)
        var fired = 0
        val subscription = n.PropertyChanged.subscribe { _, _ -> fired++ }
        c.Add(10)                                     // raises PropertyChanged -> handler fires
        c.Add(20)
        subscription.close()
        c.Add(30)                                     // handler no longer fires
        assertEquals(3, c.Count)                      // count=3
        assertTrue(fired > 0)                         // fired=true (raised while subscribed)
    }

    @TestAttribute
    fun subscriptionCloseRemovesTheExactLambdaOnce() {
        val c = ObservableCollection<Int>()
        var fired = 0
        val subscription = c.CollectionChanged.subscribe { _, _ -> fired++ }

        c.Add(10)
        assertEquals(1, fired)
        subscription.close()
        subscription.close()                         // idempotent
        c.Add(20)
        assertEquals(1, fired)
    }

    @TestAttribute
    fun subscriptionParticipatesInUse() {
        val c = ObservableCollection<Int>()
        var fired = 0

        c.CollectionChanged.subscribe { _, _ -> fired++ }.use {
            c.Add(10)
        }
        c.Add(20)
        assertEquals(1, fired)
    }

    @TestAttribute
    fun subscriptionClosesInGenericContext() {
        val c = ObservableCollection<String>()
        var fired = 0
        val subscription = subscribeGeneric(c) { _, _ -> fired++ }

        c.Add("before")
        subscription.close()
        c.Add("after")
        assertEquals(1, fired)
    }
}
