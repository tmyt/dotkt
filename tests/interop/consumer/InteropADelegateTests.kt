// C#-producer roundtrip consumer battery (batch A — delegate / static-member interop). Side-effect prints in the
// original samples are captured into a value and asserted (design D1 value asserts; the lambda -> .NET delegate bind
// is exercised identically). Each producer runtime.cs has its OWN namespace.
//
//   cbk        <- il-cbk        a lambda binds to a .NET delegate param (custom delegate + BCL Action), façade-free
//   delegatearg<- il-delegatearg a lambda passed to a .NET CONSTRUCTOR and a .NET METHOD delegate param
//   delegobj   <- il-delegobj   #1: overriding a BCL virtual whose delegate param has an `object`/Any? Invoke arg
//   injstatic  <- il-injstatic  public STATIC members surfaced on a synthesized companion (implicit + `.Companion`)
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import Cbk.Engine
import Delegatearg.Box
import Delegobj.Ctx
import Injstatic.App

// il-delegobj top-level helper: override a BCL virtual whose delegate param is `(Any?) -> Unit` (the
// SynchronizationContext.Post shape). Unique name so it cannot collide with another battery's top-level decl.
class DelegobjMyCtx : Ctx() {
    override fun Post(cb: (Any?) -> Unit, state: Any?) {
        cb(state)
    }
}

class InteropADelegateTests {
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
        // implicit (no .Companion) — the form .NET code naturally reads as
        var p = 0
        App.start({ x -> p = x })                  // -> p=42
        assertEquals(42, p)
        assertEquals(7, App.Count)                 // -> 7
        assertEquals(99, App.Answer)               // -> 99  (static FIELD, surfaced as a property -> ldsfld)
        assertEquals(123, App.Magic)               // -> 123 (const/literal FIELD -> inlined value)
        // explicit .Companion — regression coverage for the original form
        var p2 = 0
        App.Companion.start({ x -> p2 = x })       // -> p=42
        assertEquals(42, p2)
        assertEquals(7, App.Companion.Count)       // -> 7
        assertEquals(99, App.Companion.Answer)     // -> 99
        assertEquals(123, App.Companion.Magic)     // -> 123
    }
}
