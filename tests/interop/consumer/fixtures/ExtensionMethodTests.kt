// C#-producer roundtrip consumer battery (batch A — extension / generic .NET interop). Consumes the producer's public
// .NET API FAÇADE-FREE (dll2klib re-imported the plain C# dll's types from `import <Ns>.<Type>`) and asserts each
// migrated CLR-interop case's golden values 1:1 (design D1 value asserts). NONE of the producer's source is in this
// compilation. Each C# runtime.cs was given its OWN namespace so the colliding simple names coexist.
//
//   c1net    <- il-c1net    façade-free .NET consumption: generic methods, params/vararg, .NET default args, op_*
//                           operators, struct value-type instance methods, C#-origin extension methods (member-import)
//   csext    <- il-csext    #137: C#-origin `[Extension]` methods brought in AS top-level extensions via `import Ns.*`
//   csextrecv<- il-csextrecv#144: same-name/same-arity `[Extension]`s on DIFFERENT receivers (class + primitive)
//   genextval<- il-genextval#157: an inferred `Cell(40)` over a projected generic must construct `Cell<int32>`
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
// il-c1net (`import Owner.member` form — the `using static` analog)
import C1Net.Vec2
import C1Net.Util
import C1Net.Ext.tripled
import C1Net.Ext.shout
// il-csext / il-csextrecv / il-genextval (`import Ns.*` — the `using Ns;` analog)
import Csext.*
import Csextrecv.*
import Genextval.*

class ExtensionMethodTests {
    @TestAttribute
    fun c1net() {
        assertEquals(42, Util.Echo(42))            // 42   (generic method)
        assertEquals("hi", Util.Echo("hi"))        // hi
        assertEquals(10, Util.Sum(1, 2, 3, 4))     // 10   (params int[])
        assertEquals(15, Util.AddDef(5))           // 15   (default arg b=10)
        assertEquals(105, Util.AddDef(5, 100))     // 105
        assertEquals(52, (Vec2(1, 2) + Vec2(3, 4)).Mag2())  // (4,6)  -> 52  (operator +, struct instance method)
        assertEquals(21, 7.tripled())              // 21   (.NET extension method, Int receiver)
        assertEquals(41, (Vec2(5, 7) - Vec2(1, 2)).Mag2())  // (4,5)  -> 41  (op_Subtraction)
        assertEquals(117, (Vec2(2, 3) * 3).Mag2()) // (6,9)  -> 117 (op_Multiply)
        assertEquals(20, (Vec2(8, 4) / 2).Mag2())  // (4,2)  -> 20  (op_Division)
        assertEquals(5, (-Vec2(1, 2)).Mag2())      // (-1,-2)-> 5   (op_UnaryNegation)
        assertEquals("yo!", "yo".shout())          // yo!  (.NET extension method, String receiver)
    }

    @TestAttribute
    fun csext() {
        val w = Csext.W(21)
        assertEquals(42, w.Twice())                // top-level extension                          -> 42
        assertEquals(22, w.PlusN(1))               // top-level extension with an arg              -> 22
        val b = Csext.Box<String>("hi")
        assertEquals("hi", b.Echo())               // generic extension `fun <T> Box<T>.Echo(): T` -> hi
    }

    @TestAttribute
    fun csextrecv() {
        val foo = Csextrecv.Foo(10)
        val bar = Csextrecv.Bar(20)
        assertEquals(11, foo.Tag())                // FooExt.Tag  -> 11
        assertEquals(120, bar.Tag())               // BarExt.Tag  -> 120
        assertEquals(30, foo.Mix(3))               // FooExt.Mix  -> 30
        assertEquals(15, bar.Mix(5))               // BarExt.Mix  -> 15
        assertEquals(4, "abcd".Kind())             // StrExt.Kind (this string) -> 4    (primitive receiver)
        assertEquals(1007, 7.Kind())               // IntExt.Kind (this int)    -> 1007 (primitive receiver)
    }

    @TestAttribute
    fun genextval() {
        val c = Genextval.Cell(40)
        assertEquals(40, c.V)                      // 40   (field read of the reified value arg)
        assertEquals(41, c.Peek())                 // 41   (extension bound to Cell<int32>: c.V + 1)
    }
}
