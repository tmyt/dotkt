// `System.Console.WriteLine` called explicitly, and with it the .NET half of the omitted-`vararg` fill.
//
// Every reference assembly is projected COMPLETELY, so `Console.WriteLine`'s `params object?[]?` overload is one of the
// frontend's candidates. For a NON-NULL `String` argument Kotlin picks it over `WriteLine(value: String?)`: `String` is
// a strict subtype of `String?`, which makes the two-parameter candidate the more specific one, and the vararg
// tie-break never applies. That is ordinary Kotlin resolution over a faithful projection, so the backend's job is to
// fill the omitted vararg with Kotlin's empty array — which is exactly what it failed to do, refusing the call at CIL
// emission with an argument-count mismatch. `Console.WriteLine("x")` is therefore this family's canonical shape.
//
// The Kotlin-language half of the omitted-vararg family lives in tests/basic/fixtures/VarargOmissionTests.kt.
//
// Top-level names are family-prefixed with `bclConsole` (one assembly = one namespace).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Console
import System.Environment
import System.IO.Path
import System.IO.StringWriter

// Run `body` with `System.Console.Out` redirected, and hand back what it wrote. The writer is restored in a `finally`
// so a failing assertion cannot leave the suite's own console captured.
private fun bclConsoleCapture(body: () -> Unit): String {
    val previous = Console.Out
    val buffer = StringWriter()
    try {
        Console.SetOut(buffer)
        body()
    } finally {
        Console.SetOut(previous)
    }
    return buffer.ToString()
}

class BclConsoleWriteTests {
    // The reported shape (fill in a TRAILING slot), the supplied form of the same overload, and the two controls that
    // prove the fill did not over-fire: a zero-parameter overload must gain no argument, and a `String?` argument must
    // still reach the non-vararg `WriteLine(value)` — the resolution split the whole diagnosis rests on.
    @TestAttribute
    fun writeLineSelectsAndFillsTheParamsOverload() {
        val nl = Environment.NewLine
        assertEquals("hello$nl", bclConsoleCapture { Console.WriteLine("hello") })
        assertEquals("a-b$nl", bclConsoleCapture { Console.WriteLine("{0}-{1}", "a", "b") })
        assertEquals(nl, bclConsoleCapture { Console.WriteLine() })
        val maybe: String? = "n"
        assertEquals("n$nl", bclConsoleCapture { Console.WriteLine(maybe) })
    }

    // The user-visible consequence of that resolution (docs/dotkt-semantics.md §8g): the selected overload FORMATS its
    // first argument, so a doubled brace is an escape. Fails if resolution ever moves to `WriteLine(String?)`, which
    // the assertions above would not notice.
    @TestAttribute
    fun theSelectedOverloadFormatsItsArgument() {
        assertEquals("{literal}" + Environment.NewLine, bclConsoleCapture { Console.WriteLine("{{literal}}") })
    }

    // A projected `params` member whose variadic parameter is the ONLY one, so the fill lands in slot 0 rather than a
    // trailing slot — and nothing about it is Console-specific.
    @TestAttribute
    fun paramsMemberWithTheVariadicParameterFirst() {
        assertEquals("", Path.Combine())
        assertEquals("a", Path.Combine("a"))
    }
}
