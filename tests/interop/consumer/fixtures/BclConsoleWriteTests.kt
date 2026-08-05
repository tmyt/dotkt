// `System.Console.WriteLine` called explicitly, and with it the .NET half of the omitted-`vararg` fill.
//
// Every reference assembly is projected completely, including `Console.WriteLine(format, params object?[]?)`. #367
// also gives the fixed `WriteLine(string? value)` a metadata-only `String` view when the competing params prefix has
// the same CLR type and differs only by NRT. Stock Kotlin resolution can then apply its non-vararg tiebreak, matching
// C# without hiding either physical declaration. Explicit supplied/spread params calls remain available.
//
// The Kotlin-language half of the omitted-vararg family lives in tests/basic/fixtures/VarargOmissionTests.kt.
//
// Top-level names are family-prefixed with `bclConsole` (one assembly = one namespace).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
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
    // Non-null and nullable Strings both reach the fixed physical overload; supplied and explicit-empty params calls
    // still reach the formatting overload. A zero-parameter overload must gain no argument.
    @TestAttribute
    fun writeLineMatchesCSharpForTheNrtOnlyOverloadFamily() {
        val nl = Environment.NewLine
        assertEquals("hello$nl", bclConsoleCapture { Console.WriteLine("hello") })
        assertEquals("a-b$nl", bclConsoleCapture { Console.WriteLine("{0}-{1}", "a", "b") })
        assertEquals("{literal}$nl", bclConsoleCapture {
            Console.WriteLine("{{literal}}", *emptyArray<Any?>())
        })
        assertEquals(nl, bclConsoleCapture { Console.WriteLine() })
        val maybe: String? = "n"
        assertEquals("n$nl", bclConsoleCapture { Console.WriteLine(maybe) })
    }

    // The fixed overload treats braces literally. This used to throw FormatException when Kotlin selected the empty
    // expanded params form; the exact observable is stronger than merely checking identical text such as "hello".
    @TestAttribute
    fun theFixedOverloadDoesNotInterpretAFormatString() {
        assertEquals("{0}" + Environment.NewLine, bclConsoleCapture { Console.WriteLine("{0}") })
    }

    // A projected `params` member whose variadic parameter is the ONLY one, so the fill lands in slot 0 rather than a
    // trailing slot — and nothing about it is Console-specific.
    @TestAttribute
    fun paramsMemberWithTheVariadicParameterFirst() {
        assertEquals("", Path.Combine())
        assertEquals("a", Path.Combine("a"))
    }
}
