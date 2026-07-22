// .NET event-CONSUME battery (batch IntropD) — migrates the facadegen `import System.*` event-consumer cases/il-*
// onto the in-process NUnit suite. Each old case's `main` + stdout-golden becomes one @TestAttribute method whose
// per-value assert is strictly stronger (typed Int/Boolean) than the old string diff. Every value the old case
// asserted is preserved 1:1 (see the `// <expected>` comments); the side-effecting handler `println`s that the old
// golden pinned by ORDER ("changed" / "h fired") become deterministic per-handler counters (synchronous raise) so the
// exact fire multiplicities are asserted rather than an ordered stdout dump.
//
// These CONSUME .NET events (`+=` / `-=` on an ObservableCollection<T>); they are NOT ClrEventTests, which is about
// a Kotlin class IMPLEMENTING a .NET interface event. bir2cir's ClrEventOperatorBinding binds `+=`/`-=` to the
// event's add/remove accessor; ObservableCollection.Add raises the event SYNCHRONOUSLY on the calling thread, so the
// handler fires deterministically with no UI loop.
//
// Coverage preserved (old case -> method):
//   il-event      -> instanceEvent_addRemove       .NET INSTANCE event ObservableCollection.CollectionChanged `+=`/`-=` (ClrEvent<T> property handle)
//   il-ifaceevent -> interfaceEvent_addRemove       INTERFACE .NET event INotifyPropertyChanged.PropertyChanged via ObservableCollection's explicit impl (callvirt on the interface slot)
//
// Top-level names are family-prefixed with `IntropD` (one assembly = one namespace) to avoid clashing with sibling
// batteries and the stdlib.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import System.Collections.ObjectModel.ObservableCollection
import System.ComponentModel.INotifyPropertyChanged

class ObservableCollectionEventTests {
    private fun <T> subscribeGeneric(
        collection: ObservableCollection<T>,
        handler: (Any?, Any?) -> Unit,
    ): AutoCloseable = collection.CollectionChanged.subscribe(handler)

    // il-event: a direct lambda literal (`button.Click += { }`) AND a stored handler reference (needed for `-=`
    // delegate equality) subscribe/unsubscribe on a .NET INSTANCE event; Add() raises CollectionChanged synchronously.
    @TestAttribute
    fun instanceEventAddRemove() {
        val c = ObservableCollection<Int>()
        var changed = 0
        // (1) a direct lambda literal bound straight into the event delegate type.
        c.CollectionChanged += { _, _ -> changed++ }
        c.Add(10)                                     // -> "changed"
        c.Add(20)                                     // -> "changed"
        assertEquals(2, changed)                      // literal fired twice
        assertEquals(2, c.Count)                      // 2

        // (2) a stored handler reference so it can later be removed (`-=` needs delegate equality).
        var hFired = 0
        val h: (Any?, Any?) -> Unit = { _, _ -> hFired++ }
        c.CollectionChanged += h
        c.Add(30)                                     // literal + h both fire -> "changed", "h fired"
        assertEquals(3, changed)                      // literal fired again
        assertEquals(1, hFired)                        // h fired once
        c.CollectionChanged -= h
        c.Add(40)                                     // only the literal fires -> "changed"
        assertEquals(4, changed)                       // literal fired again
        assertEquals(1, hFired)                        // h did NOT fire after `-=`
        assertEquals(4, c.Count)                       // 4
    }

    // il-ifaceevent: subscribe/unsubscribe with `+=`/`-=` on an INTERFACE-typed receiver (explicit interface impl).
    // ObservableCollection<T> implements INotifyPropertyChanged explicitly; the interface-typed view exposes
    // PropertyChanged, and Add() raises it (a callvirt on the interface slot).
    @TestAttribute
    fun interfaceEventAddRemove() {
        val c = ObservableCollection<Int>()
        val n: INotifyPropertyChanged = c             // interface-typed receiver (explicit interface implementation)
        var fired = 0
        val h: (Any?, Any?) -> Unit = { _, _ -> fired++ }
        n.PropertyChanged += h                        // subscribe on the INTERFACE-typed receiver
        c.Add(10)                                     // raises PropertyChanged -> handler fires
        c.Add(20)
        n.PropertyChanged -= h                        // unsubscribe (delegate equality)
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
