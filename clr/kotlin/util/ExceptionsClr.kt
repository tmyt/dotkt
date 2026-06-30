@file:Suppress(
    "ACTUAL_WITHOUT_EXPECT",
    "NO_ACTUAL_FOR_EXPECT",
    "UNCHECKED_CAST",
    "NOTHING_TO_INLINE",
    "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS",
    "PLATFORM_CLASS_MAPPED_TO_KOTLIN",
)
// Throwable is mapped to System.Exception; the stack-trace text comes from Exception.ToString().
// See docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP".

package kotlin

// --- @Clr BCL primitives ----------------------------------------------------------------------
// The standard error stream is System.Console.Error (a static TextWriter property getter).

@kotlin.clr.ClrIntrinsic("System.IO.TextWriter")
private class ClrTextWriter {
    @kotlin.clr.ClrIntrinsic("WriteLine")
    fun writeLine(value: String?): Unit = TODO("clr binding should be implemented")
}

@kotlin.clr.ClrIntrinsic("System.Console.get_Error")
private fun clrStdErr(): ClrTextWriter = TODO("clr binding should be implemented")

@PublishedApi
internal fun printStackTraceImpl(throwable: Throwable): Unit = clrStdErr().writeLine(throwable.stackTraceToString())

@kotlin.internal.InlineOnly
public actual inline fun Throwable.printStackTrace(): Unit = printStackTraceImpl(this)

// System.Exception.ToString() renders the message and the stack trace (the .NET text differs from the JVM's).
@SinceKotlin("1.4")
@kotlin.clr.ClrIntrinsic("ToString")
public actual fun Throwable.stackTraceToString(): String = TODO("clr binding should be implemented")

// Suppressed exceptions are not tracked on the CLR (a ConditionalWeakTable side-table would be needed); this is a
// no-op, and `suppressedExceptions` always reports an empty list. Functionally correct for `use {}`'s closeFinally.
@SinceKotlin("1.1")
@kotlin.internal.HidesMembers
public actual fun Throwable.addSuppressed(exception: Throwable) { }

@SinceKotlin("1.4")
public actual val Throwable.suppressedExceptions: List<Throwable>
    get() = emptyList()
