// CLR value-type / intrinsic interop battery (feature fixture), resolved through reference KLIBs.
//
// Coverage preserved (old case -> method):
//   il-vtboundref  -> boundRefOverStruct        #149 a bound callable-ref over a VALUE-TYPE (.NET struct, System.TimeSpan) receiver — a compiler-owned closure captures the value, including a nullable receiver unwrapped by bir2cir
//   il-inlonlyintr -> stringBuilderIndexerSet   #40 a cross-module @InlineOnly + @ClrIntrinsic("set_Chars") stdlib member (StringBuilder.set) keeps its BCL binding across the assembly boundary — kotc carries the annotation as OPAQUE ref.dll metadata, bir2cir's MemberCallSubstitution binds the plain call
//
// PARTIAL DUP — il-inlonlyintr's Char-predicate lines (isLetter/isDigit/isLetterOrDigit) already live in
// CharacterAndCaseMappingTests.kt; only the unique `sb[0]='X'` StringBuilder indexer-set (@InlineOnly @ClrIntrinsic) is
// migrated here.
//
// Top-level names are family-prefixed with `BclValueType` (one assembly = one namespace).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.TimeSpan

class BclValueTypeInteropTests {
    // il-vtboundref: a bound callable-reference over a value-type receiver captured by the generated adapter.
    @TestAttribute
    fun boundRefOverStruct() {
        val a = TimeSpan(0, 0, 5)
        val b = TimeSpan(0, 0, 9)
        assertEquals("00:00:05", a.ToString())
        val cmp: (TimeSpan) -> Int = a::CompareTo   // non-virtual struct call through the captured receiver
        assertEquals(-1, cmp(b))                    // -1
        val g: () -> String = a::ToString           // constrained Object.ToString slot over the captured TimeSpan
        assertEquals("00:00:05", g())               // 00:00:05
        val nullable: TimeSpan? = a
        val fromNullable: () -> String = nullable!!::ToString
        assertEquals("00:00:05", fromNullable())    // CLR delegate nodes carry their owner in clrType, not type
    }

    // il-inlonlyintr: `sb[0]='X'` is StringBuilder.set (@InlineOnly @ClrIntrinsic("set_Chars")) — the canonical #40
    // case: the intrinsic binding survives the cross-module ref.dll round-trip. (The isLetter/isDigit/isLetterOrDigit
    // siblings are covered by CharacterAndCaseMappingTests.kt.)
    @TestAttribute
    fun stringBuilderIndexerSet() {
        val sb = StringBuilder("abc")
        sb[0] = 'X'                                 // StringBuilder.set -> System.Text.StringBuilder.set_Chars
        assertEquals("Xbc", sb.toString())          // Xbc
    }
}
