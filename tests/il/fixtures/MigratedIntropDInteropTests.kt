// CLR value-type / intrinsic interop battery (batch IntropD) — migrates the facadegen `import System.*` interop
// cases/il-* onto the in-process NUnit suite. Each old case's `main` + stdout-golden becomes one @TestAttribute
// method whose per-value assert is strictly stronger (typed) than the old string diff; every asserted value is
// preserved 1:1 (see the `// <expected>` comments).
//
// Coverage preserved (old case -> method):
//   il-vtboundref  -> boundRefOverStruct        #149 a bound callable-ref over a VALUE-TYPE (.NET struct, System.TimeSpan) receiver — the struct is BOXED before the delegate ctor (ilemit newBoundClrDelegate); covers non-virtual (ldftn) AND virtual (ldvirtftn) targets
//   il-inlonlyintr -> stringBuilderIndexerSet   #40 a cross-module @InlineOnly + @ClrIntrinsic("set_Chars") stdlib member (StringBuilder.set) keeps its BCL binding across the assembly boundary — kotc carries the annotation as OPAQUE ref.dll metadata, bir2cir's MemberCallSubstitution binds the plain call
//
// PARTIAL DUP — il-inlonlyintr's Char-predicate lines (isLetter/isDigit/isLetterOrDigit) already live in
// MigratedM1CharTests.kt; only the unique `sb[0]='X'` StringBuilder indexer-set (@InlineOnly @ClrIntrinsic) is
// migrated here.
//
// Top-level names are family-prefixed with `IntropD` (one assembly = one namespace).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.TimeSpan

class MigratedIntropDInteropTests {
    // il-vtboundref: a bound callable-reference over a value-type receiver — the struct is boxed before binding.
    @TestAttribute
    fun boundRefOverStruct() {
        val a = TimeSpan(0, 0, 5)
        val b = TimeSpan(0, 0, 9)
        val cmp: (TimeSpan) -> Int = a::CompareTo   // non-virtual struct method -> box + ldftn
        assertEquals(-1, cmp(b))                    // -1
        val g: () -> String = a::ToString           // virtual (Object.ToString override) -> box + dup + ldvirtftn
        assertEquals("00:00:05", g())               // 00:00:05
    }

    // il-inlonlyintr: `sb[0]='X'` is StringBuilder.set (@InlineOnly @ClrIntrinsic("set_Chars")) — the canonical #40
    // case: the intrinsic binding survives the cross-module ref.dll round-trip. (The isLetter/isDigit/isLetterOrDigit
    // siblings are covered by MigratedM1CharTests.kt.)
    @TestAttribute
    fun stringBuilderIndexerSet() {
        val sb = StringBuilder("abc")
        sb[0] = 'X'                                 // StringBuilder.set -> System.Text.StringBuilder.set_Chars
        assertEquals("Xbc", sb.toString())          // Xbc
    }
}
