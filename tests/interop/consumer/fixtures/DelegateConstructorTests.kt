// C#-producer roundtrip consumer battery (batch A — delegate / static-member interop). Side-effect prints in the
// original samples are captured into a value and asserted (design D1 value asserts; the lambda -> .NET delegate bind
// is exercised identically). Each producer runtime.cs has its OWN namespace.
//
//   cbk        <- il-cbk        a lambda binds to a .NET delegate param (custom delegate + BCL Action), façade-free
//   delegatearg<- il-delegatearg a lambda passed to a .NET CONSTRUCTOR and a .NET METHOD delegate param
//   delegobj   <- il-delegobj   #1: overriding a BCL virtual whose delegate param has an `object`/Any? Invoke arg
//   injstatic  <- il-injstatic  public STATIC members surfaced directly on their declaring type
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import Cbk.Engine
import Delegatearg.Box
import Delegobj.Ctx
import Delegobj.PostCb
import Delegobj.PostCbTwin
import Delegobj.RecursiveCb
import Delegobj.SameShape
import Injstatic.App
import Injstatic.GenericApp
import System.Threading.SendOrPostCallback
import System.Threading.SynchronizationContext

// A projected CLR delegate is a nominal callable SAM type. The source override and the emitted CLR slot therefore
// name the same PostCb identity; its operator invoke remains Kotlin-callable.
class DelegobjMyCtx : Ctx() {
    override fun Post(cb: PostCb, state: Any?) {
        cb(state)
    }
}

class DelegobjSynchronizationContext : SynchronizationContext() {
    override fun Post(d: SendOrPostCallback, state: Any?) {
        d(state)
    }
}

class DelegateConstructorTests {
    @TestAttribute
    fun cbk() {
        val e = Engine()
        assertEquals("=v42", e.Apply(21) { x -> "v" + (x * 2) })  // =v42 — lambda -> custom delegate Transform
        var ran = ""
        e.Run { ran = "ran" }                                     // lambda -> System.Action
        assertEquals("ran", ran)                                  // ran
    }

    @TestAttribute
    fun delegatearg() {
        val b = Box({ x -> x + 1 })                // delegate as ctor arg
        assertEquals(42, b.Apply(41))              // 42
        assertEquals(20, b.Run({ x -> x * 2 }))    // delegate as method arg -> g(10)=20
        val c = Box({ x -> x * x })
        assertEquals(81, c.Apply(9))               // 81
    }

    @TestAttribute
    fun delegobj() {
        val c = DelegobjMyCtx()
        var out = ""
        c.Post(PostCb { s -> out = "posted: $s" }, 42)    // posted: 42
        assertEquals("posted: 42", out)
        (c as Ctx).Post(PostCb { s -> out = "base-typed: $s" }, 7)  // virtual dispatch through Ctx
        assertEquals("base-typed: 7", out)

        val sync: SynchronizationContext = DelegobjSynchronizationContext()
        sync.Post(SendOrPostCallback { s -> out = "system: $s" }, 9)
        assertEquals("system: 9", out)

        assertEquals("post", SameShape.Pick(PostCb { s -> out = "same: $s" }))
        assertEquals("same: first", out)
        assertEquals("twin", SameShape.Pick(PostCbTwin { s -> out = "same: $s" }))
        assertEquals("same: second", out)

        var recursiveCalls = 0
        val leaf = RecursiveCb { _ -> recursiveCalls += 1 }
        val root = RecursiveCb { next -> recursiveCalls += 1; next(leaf) }
        root(leaf)
        assertEquals(2, recursiveCalls)
    }

    @TestAttribute
    fun injstatic() {
        // CLR statics remain direct KLIB static declarations; no synthetic companion value exists.
        var p = 0
        App.start({ x -> p = x })                  // -> p=42
        assertEquals(42, p)
        assertEquals(7, App.Count)                 // -> 7
        assertEquals(99, App.Answer)               // -> 99  (static FIELD, surfaced as a property -> ldsfld)
        assertEquals(123, App.Magic)               // -> 123 (const/literal FIELD -> inlined value)
        App.Mutable = 11
        assertEquals(11, App.Mutable)              // mutable static FIELD -> stsfld / ldsfld
        GenericApp.Mutable = 13
        assertEquals(13, GenericApp.Mutable)       // generic owner -> representative GenericApp<object>
        val countRef = App::Count
        assertEquals(7, countRef.get())
        val mutableRef = App::Mutable
        mutableRef.set(17)
        assertEquals(17, mutableRef.get())          // direct CLR static KMutableProperty0
    }
}
