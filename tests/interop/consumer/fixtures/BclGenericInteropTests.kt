// Generic / enum BCL-interop battery (feature fixture). These fixtures import real .NET types through
// the Interop consumer's reference KLIBs and cover generic types, methods, delegates, and enums.
//
// Coverage preserved (old case -> method):
//   il-forin        -> forin_netEnumerableForLoop   for-in over a real .NET IEnumerable<T> (List<Int>) -> GetEnumerator/MoveNext/Current
//   il-gendelegate  -> gendelegate_lambdaToGenericDelegateCtor  #140 lambda -> generic BCL delegate ctor over a USER type (Func<Box>/Action<Box>)
//   il-jsongeneric  -> jsongeneric_genericMethodInteropSibling  #44 generic .NET method whose sibling param is a reference-KLIB-projected interop type
//   il-netenumbound -> netenumbound_boundEnumTypeParam           .NET enum bound to a Kotlin `<T : Enum<T>>` param + enumValues/enumValueOf
//
// Top-level names are family-prefixed with `BclGenericInterop` (one assembly = one namespace) to avoid clashing with sibling
// batteries and the stdlib.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import System.Collections.Generic.List          // il-forin — the raw .NET List<T> (bare `List` in this file)
import System.Threading.ThreadLocal             // il-gendelegate — Func<T> value-factory ctor
import System.Progress                          // il-gendelegate — Action<T> handler ctor
import System.Text.Json.JsonSerializer          // il-jsongeneric
import System.Text.Json.JsonSerializerOptions   // il-jsongeneric
import System.DayOfWeek                          // il-netenumbound

// il-gendelegate : a same-assembly (TypeBuilder) USER type used as the delegate's generic arg.
class BclGenericInteropBox(val n: Int)

// il-netenumbound : a Kotlin generic bounded by Enum<T>, applied to a .NET enum.
fun <T : Enum<T>> bclGenericInteropNameOf(e: T): String = e.name

class BclGenericInteropTests {
    // il-forin: for-in over a real .NET IEnumerable<T> (System.Collections.Generic.List<Int>) lowers to the
    // GetEnumerator/MoveNext/Current reverse bridge.
    @TestAttribute
    fun netEnumerableForLoop() {
        val l = List<Int>()
        l.Add(10); l.Add(20); l.Add(30)

        var sum = 0
        for (x in l) sum += x
        assertEquals(60, sum)               // 60

        var joined = ""
        for (x in l) joined += "$x,"
        assertEquals("10,20,30,", joined)   // 10,20,30,
        assertEquals(3, l.Count)            // 3
    }

    // il-gendelegate: a Kotlin lambda passed to a GENERIC BCL delegate ctor param over a USER type — Func<Box> (return
    // position) and Action<Box> (input position); ilemit substitutes T -> Box on the open delegate definition.
    @TestAttribute
    fun lambdaToGenericDelegateCtor() {
        val tl = ThreadLocal<BclGenericInteropBox>({ BclGenericInteropBox(42) })   // Func<T> — return-position type-var substitution
        assertEquals(42, tl.Value.n)                            // 42
        var seen = 0
        val pr = Progress<BclGenericInteropBox>({ b: BclGenericInteropBox -> seen = b.n })  // Action<T> — input-position substitution
        assertTrue(pr != null)                                  // True
    }

    // il-jsongeneric: #44 a generic .NET method (JsonSerializer.Serialize<T>) whose SIBLING param is a reference-KLIB-projected
    // interop type (JsonSerializerOptions) — ShapeSynthesis resolves the leaf to its .NET simple name so the overload binds.
    @TestAttribute
    fun genericMethodInteropSibling() {
        val opts = JsonSerializerOptions()
        opts.WriteIndented = false
        assertEquals("42", JsonSerializer.Serialize<Int>(42, opts))       // 42
        assertEquals("\"hi\"", JsonSerializer.Serialize<String>("hi", opts))  // "hi"
    }

    // il-netenumbound: a .NET enum (System.DayOfWeek) bound to a Kotlin `<T : Enum<T>>` type parameter, plus the
    // stdlib enumValues/enumValueOf reified intrinsics over the .NET enum.
    @TestAttribute
    fun boundEnumTypeParam() {
        val d: DayOfWeek = DayOfWeek.Friday
        assertEquals("Friday", bclGenericInteropNameOf(d))                    // Friday
        assertEquals(7, enumValues<DayOfWeek>().size)               // 7
        assertEquals(1, enumValueOf<DayOfWeek>("Monday").ordinal)   // 1
    }
}
