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

@kotlin.clr.ClrTypeAlias("System.IO.TextWriter")
private class ClrTextWriter {
    @kotlin.clr.ClrIntrinsic("WriteLine")
    fun writeLine(value: String?): Unit = TODO("clr binding should be implemented")
}

@kotlin.clr.ClrIntrinsic("System.Console.get_Error")
private fun clrStdErr(): ClrTextWriter = TODO("clr binding should be implemented")

// The stack-trace text is written to Console.Error. Exposed as @PublishedApi internal so the Throwable-class member
// (builtins/Throwable.kt) — which the frontend resolves to over this extension (java.lang.Throwable.printStackTrace is a
// mapped MEMBER and members win over extensions) — can share the same real body.
@PublishedApi
internal fun printStackTraceImpl(throwable: Throwable): Unit = clrStdErr().writeLine(throwable.stackTraceToString())

// NOT inline: an @InlineOnly `inline` actual is not inlined cross-module here. Kept as a plain extension for the
// expect/actual; app call sites resolve to the Throwable MEMBER (see builtins/Throwable.kt) which shares printStackTraceImpl.
public actual fun Throwable.printStackTrace(): Unit = printStackTraceImpl(this)

// System.Exception.ToString() renders the message and the stack trace (the .NET text differs from the JVM's).
@SinceKotlin("1.4")
@kotlin.clr.ClrIntrinsic("ToString")
public actual fun Throwable.stackTraceToString(): String = TODO("clr binding should be implemented")

// Ephemeron keys preserve CLR exception identity without keeping otherwise unreachable
// exception graphs alive. A suppressed exception may itself refer back to the primary.
@kotlin.clr.ClrTypeAlias("System.Runtime.CompilerServices.ConditionalWeakTable")
private class SuppressedExceptionsTable<K : Any, V : Any> {
    constructor()
    @kotlin.clr.ClrIntrinsic("GetOrCreateValue")
    fun get(key: K): V = error("CLR binding")
}

private class SuppressedExceptionsCell public constructor() {
    val exceptions = ArrayList<Throwable>()
}

private val suppressedExceptionsTable = SuppressedExceptionsTable<Throwable, SuppressedExceptionsCell>()

@kotlin.clr.ClrIntrinsic("System.Threading.Monitor.Enter")
private fun enterSuppressedExceptions(cell: Any): Unit = error("CLR binding")
@kotlin.clr.ClrIntrinsic("System.Threading.Monitor.Exit")
private fun exitSuppressedExceptions(cell: Any): Unit = error("CLR binding")

@SinceKotlin("1.1")
@kotlin.internal.HidesMembers
public actual fun Throwable.addSuppressed(exception: Throwable) {
    // Match the Kotlin extension's self-suppression policy, not Java's raw member.
    if (this === exception) return
    val cell = suppressedExceptionsTable.get(this)
    enterSuppressedExceptions(cell)
    try {
        cell.exceptions.add(exception)
    } finally { exitSuppressedExceptions(cell) }
}

@SinceKotlin("1.4")
public actual val Throwable.suppressedExceptions: List<Throwable>
    get() {
        val cell = suppressedExceptionsTable.get(this)
        enterSuppressedExceptions(cell)
        try {
            return cell.exceptions.toList()
        } finally { exitSuppressedExceptions(cell) }
    }
