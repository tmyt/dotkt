// .NET-interop battery (dual representation) — migrates cases/il-dualrep. `import System.Text.StringBuilder`
// (raw .NET view) and the stdlib's default `kotlin.text.StringBuilder` (@ClrTypeAlias-bound) are TWO distinct
// frontend types over the SAME CLR runtime type; an explicit cast crosses the views (the runtime checkcast
// always succeeds since both erase to System.Text.StringBuilder). See docs/dotkt-semantics.md.
import System.Text.StringBuilder
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert

fun useKt(sb: kotlin.text.StringBuilder): String = sb.toString()

class InteropDualRepTests {
    @TestAttribute
    fun rawNetView() {
        val net = StringBuilder()
        net.Append("net")
        ClassicAssert.AreEqual("net", net.ToString())
        ClassicAssert.AreEqual(3, net.Length)
    }

    @TestAttribute
    fun stdlibViewAndEscapeHatch() {
        // stdlib view: buildString works on kotlin.text.StringBuilder.
        val s = buildString { append("kt") }
        ClassicAssert.AreEqual("kt", s)
        // Escape hatch: an explicit cast crosses the two views (both erase to System.Text.StringBuilder).
        val net = StringBuilder()
        net.Append("net")
        @Suppress("CAST_NEVER_SUCCEEDS")
        val kt = net as kotlin.text.StringBuilder
        ClassicAssert.AreEqual("net", useKt(kt))
    }
}
