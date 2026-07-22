// .NET-interop battery (batch MigM, from cases/m-i1) — facadegen `import System.X` injects the real BCL
// System.Text.StringBuilder façade-free (no @Clr facade); Append/ToString/Length route as direct .NET members,
// and the fluent Append chain returns the same builder. Migrated onto the in-process NUnit suite; each old case's
// `main` + il_check golden becomes one @TestAttribute method whose per-value assert is strictly stronger (typed)
// than the old stdout diff; every asserted value preserved 1:1 (see `// <expected>`).
//
// Coverage preserved (old case -> method):
//   m-i1  -> stringBuilderInterop   System.Text.StringBuilder fluent Append (String + Int) / ToString / Length
//
// Top-level names are MigM-prefixed (one assembly = one namespace).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Text.StringBuilder

class BclStringBuilderTests {
    @TestAttribute
    fun stringBuilderInterop() {
        val sb = StringBuilder()
        sb.Append("Hello").Append(", ").Append("CLR ").Append(42)  // fluent chain returns the same builder
        assertEquals("Hello, CLR 42", sb.ToString())               // Hello, CLR 42
        assertEquals(13, sb.Length)                                // length = 13
    }
}
