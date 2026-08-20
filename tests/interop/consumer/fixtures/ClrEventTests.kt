// CLR event model battery (§7 of docs/design-clr-event-model.md, #187) — the MVVM conformance: a Kotlin class that
// IMPLEMENTS a .NET interface event (`class ViewModelBase : INotifyPropertyChanged { override val PropertyChanged by
// clrEvent() }`), RAISES it from OUTSIDE (a property-delegate `setValue` on a DIFFERENT type calling
// `vm.PropertyChanged.invoke(...)`, the deliberate CLR-native deviation §6), and CONSUMES it through the
// `INotifyPropertyChanged` interface slot (`subscribe`/`close`).
//
// Pass criteria (all asserted below): the type LOADS (no TypeLoadException — the synthesized add_/remove_ satisfy the
// interface slots), subscribe/close removes the exact handler, the raise carries the KProperty name, an
// unchanged-value assignment does NOT raise, and a raise with zero subscribers is a safe no-op (the `field?.Invoke`
// null-conditional). This is the first-class WPF/Avalonia MVVM spine on Kotlin-on-CLR.
//
// NB: the design's §7 snippet uses a GENERIC property delegate (`viewModelProperty<T>`); that is deferred because a
// generic delegated property (`var x by genericDelegate()`) is a SEPARATE pre-existing codegen bug (BadImageFormat,
// `found 'string' expected '!0'`) unrelated to the event model. This battery uses a NON-generic `StringVmProperty`
// delegate — it exercises the identical event path (implement / raise-from-outside-a-different-type / consume) without
// tripping the generic-delegate defect.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.ComponentModel.INotifyPropertyChanged
import System.ComponentModel.PropertyChangedEventArgs
import EventDelegation.EventSource
import kotlin.clr.ClrEvent
import kotlin.clr.clrEvent
import kotlin.reflect.KProperty

// IMPLEMENT — synthesize add_/remove_/raise_PropertyChanged + the backing delegate field + the `.event` metadata.
open class ViewModelBase : INotifyPropertyChanged {
    override val PropertyChanged: ClrEvent<(Any?, PropertyChangedEventArgs) -> Unit> by clrEvent()
}

// A property delegate that RAISES the ViewModel's event from OUTSIDE the declaring type (a DIFFERENT class) — the §6
// interop-first deviation that makes the MVVM `by …Property(...)` pattern work. Non-generic (String) on purpose.
class StringVmProperty(private val vm: ViewModelBase, initial: String) {
    private var value = initial
    operator fun getValue(r: Any?, p: KProperty<*>): String = value
    operator fun setValue(r: Any?, p: KProperty<*>, nv: String) {
        if (value != nv) {
            value = nv
            vm.PropertyChanged.invoke(vm, PropertyChangedEventArgs(p.name))   // RAISE from outside
        }
    }
}

fun ViewModelBase.stringVmProperty(initial: String) = StringVmProperty(this, initial)

class PersonViewModel : ViewModelBase() {
    var name by stringVmProperty("John Doe")
    var title by stringVmProperty("")
}

class ClrEventTests {
    // The full §7 conformance: raise carries the property name, unchanged value doesn't raise, unsubscribe stops raises.
    @TestAttribute
    fun propertyChangedFiresWithPropertyName() {
        val vm = PersonViewModel()
        var fired = 0
        var lastName: String? = null
        val subscription = (vm as INotifyPropertyChanged).PropertyChanged.subscribe { _, e ->
            fired++; lastName = e.PropertyName
        }
        vm.name = "Jane Doe"
        assertEquals(1, fired)                                       // raised exactly once
        assertEquals("name", lastName)                              // args carry the KProperty name
        vm.name = "Jane Doe"                                        // unchanged value -> no raise
        assertEquals(1, fired)
        subscription.close()
        vm.name = "Bob"
        assertEquals(1, fired)                                       // unsubscribed -> no raise
    }

    // Subscribe through the Kotlin implementation type itself. This binds the synthesized local accessors while
    // preserving the interface event's named PropertyChangedEventHandler delegate identity.
    @TestAttribute
    fun localImplementationUsesInterfaceDelegate() {
        val vm = ViewModelBase()
        var fired = 0
        val subscription = vm.PropertyChanged.subscribe { _, _ -> fired++ }
        vm.PropertyChanged.invoke(vm, PropertyChangedEventArgs("direct"))
        assertEquals(1, fired)
        subscription.close()
        vm.PropertyChanged.invoke(vm, PropertyChangedEventArgs("after-close"))
        assertEquals(1, fired)
    }

    // Two distinct properties raise with their own names through one shared event.
    @TestAttribute
    fun eachPropertyRaisesItsOwnName() {
        val vm = PersonViewModel()
        val names = ArrayList<String>()
        val subscription = (vm as INotifyPropertyChanged).PropertyChanged.subscribe { _, e ->
            e.PropertyName?.let { names.add(it) }
        }
        vm.name = "Ann"
        vm.title = "Dr"
        assertEquals(2, names.size)
        assertEquals("name", names[0])
        assertEquals("title", names[1])
        subscription.close()
    }

    // Multiple subscribers all fire (the CAS Delegate.Combine multicast), and removing one leaves the other.
    @TestAttribute
    fun multipleSubscribersMulticast() {
        val vm = PersonViewModel()
        var a = 0
        var b = 0
        val np = vm as INotifyPropertyChanged
        val sa = np.PropertyChanged.subscribe { _, _ -> a++ }
        val sb = np.PropertyChanged.subscribe { _, _ -> b++ }
        vm.name = "X"
        assertEquals(1, a)
        assertEquals(1, b)
        sa.close()
        vm.name = "Y"
        assertEquals(1, a)                                          // first subscription removed
        assertEquals(2, b)                                          // second still subscribed
        sb.close()
    }

    // A raise with ZERO subscribers is a safe no-op (the `field?.Invoke` null-conditional), never an NRE.
    @TestAttribute
    fun raiseWithNoSubscribersIsNoOp() {
        val vm = PersonViewModel()
        vm.name = "Solo"                                            // no subscriber -> no throw
        assertEquals("Solo", vm.name)
        assertTrue(true)
    }

    // #186: class delegation must forward a CLR interface event's add/remove accessors to the delegate field.
    @TestAttribute
    fun classDelegationForwardsEventSubscription() {
        val source = EventSource<Int>()
        val delegated = DerivedDelegatingEventSource<String, Int>(source)
        var total = 0
        assertEquals(42, delegated.add_Changed())
        val subscription = delegated.Changed.subscribe { value -> total += value }

        source.Fire(7)
        assertEquals(7, total)
        assertEquals(1, source.AddCount)
        subscription.close()
        assertEquals(1, source.RemoveCount)
        source.Fire(11)
        assertEquals(7, total)
    }
}
