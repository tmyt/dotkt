// A .NET VALUE TYPE (enum or struct) in the signature of an NRT-annotated member.
//
// A projected signature's nullability comes from the flattened `NullableAttribute`/`NullableContextAttribute` walk, and
// that walk holds no annotation for a value type — `String.Compare(string?, string?, StringComparison)` carries
// `[NullableContext(2)]`, which annotates the two strings and says nothing at all about the enum. Reading the context
// byte as the enum's annotation projected the parameter as `StringComparison?`, so the descriptor the frontend resolved
// named a member that does not exist and bir2cir refused the call ("no .NET member matches the resolved descriptor").
// The same walk decides byte POSITIONS, so the error was never confined to the value type itself: every later byte in
// the same slot shifted with it.
//
// One case per arm that can fail on its own: the ANNOTATION (a value type must not become nullable), the SELECTION it
// governs (the same-arity sibling must stay reachable), and the byte POSITION (a slot whose own `[Nullable]` array puts
// a value type ahead of other nodes). `Span`/`ClrRef` — the byref and byref-like arms of the same walk — already have
// owners in StackBufferTests and ByRefParameterTests.
//
// Top-level names are family-prefixed with `bclValueArg` (one assembly = one namespace).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.String as ClrString
import System.StringComparison
import System.Collections.Generic.Dictionary

class BclValueTypeArgumentTests {
    // The reported shape. Two different enum values, so the assertion proves the argument REACHES the callee rather
    // than the overload merely resolving.
    @TestAttribute
    fun enumArgumentUnderANullableContext() {
        assertEquals(-1, ClrString.Compare("a", "b", StringComparison.Ordinal))
        assertEquals(0, ClrString.Compare("a", "A", StringComparison.OrdinalIgnoreCase))
    }

    // The two-argument control, and the same-arity sibling whose third parameter is a `Boolean` in the same position:
    // the fix must not make every three-argument `Compare` resolve to the enum overload.
    @TestAttribute
    fun siblingOverloadsInTheSamePosition() {
        assertEquals(-1, ClrString.Compare("a", "b"))
        assertEquals(0, ClrString.Compare("a", "A", true))
    }

    // The byte POSITION arm, on a slot that really carries a multi-byte array: `Dictionary<K,V>(IEnumerable<
    // KeyValuePair<K,V>>)` is `[Nullable(1,0,1,1)]` — IEnumerable(1), KeyValuePair(0, a CONSTRUCTED value type that
    // DOES hold a byte), then K and V. Miscount the value type either way and `K`/`V` read the wrong bytes, so this
    // fails where the two cases above still pass.
    @TestAttribute
    fun valueTypeNestedAheadOfOtherNodes() {
        val source = Dictionary<String, String>()
        source.Add("k", "v")
        val copy = Dictionary<String, String>(source)
        assertEquals(1, copy.Count)
        assertEquals("v", copy["k"])
    }
}
