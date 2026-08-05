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
import Injstatic.App
import Injstatic.GenericApp

// il-delegobj top-level helper: override a BCL virtual whose delegate param is `(Any?) -> Unit` (the
// SynchronizationContext.Post shape). Unique name so it cannot collide with another battery's top-level decl.
class DelegobjMyCtx : Ctx() {
    override fun Post(cb: (Any?) -> Unit, state: Any?) {
        cb(state)
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
        c.Post({ s -> out = "posted: $s" }, 42)    // posted: 42
        assertEquals("posted: 42", out)
        (c as Ctx).Post({ s -> out = "base-typed: $s" }, 7)  // base-typed: 7 (virtual dispatch through Ctx)
        assertEquals("base-typed: 7", out)
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
