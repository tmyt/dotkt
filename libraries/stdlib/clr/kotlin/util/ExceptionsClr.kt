@file:Suppress(
    "ACTUAL_WITHOUT_EXPECT",
    "NO_ACTUAL_FOR_EXPECT",
    "UNCHECKED_CAST",
    "NOTHING_TO_INLINE",
    "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS",
    "PLATFORM_CLASS_MAPPED_TO_KOTLIN",
)
// Throwable maps to System.Exception. Detailed output preserves opaque descriptions
// and explicitly adds stack and logical graph details; CLR-wide ToString is unchanged.
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

private class ExceptionTraceTask(val exception: Throwable, val caption: String, val depth: Int)

// A virtual description is opaque: it may omit the stack or describe a native cause
// different from Kotlin's logical cause. Never parse it or assume it covers a child.
@SinceKotlin("1.4")
public actual fun Throwable.stackTraceToString(): String {
    val text = StringBuilder()
    val expanded = System.Collections.Generic.HashSet<Throwable>(System.Collections.Generic.ReferenceEqualityComparer.Instance)
    val pending = ArrayList<ExceptionTraceTask>()
    pending.add(ExceptionTraceTask(this, "", 0))
    while (pending.isNotEmpty()) {
        val task = pending.removeAt(pending.size - 1)
        val expand = expanded.Add(task.exception)
        // Take detached state before invoking any overridable formatting code.
        val suppressed = if (expand) task.exception.suppressedExceptions else emptyList()
        val cause = if (expand) task.exception.cause else null
        if (text.length != 0) text.append('\n')
        if (task.depth != 0) text.append(task.caption).append(" (depth ").append(task.depth).append("): ")
        text.append(if (expand) task.exception.toString() else "[circular/shared reference]")
        if (!expand) continue
        val stack = (task.exception as System.Exception).StackTrace
        if (stack != null) text.append('\n').append(stack)
        if (cause != null) pending.add(ExceptionTraceTask(cause, "Caused by", task.depth + 1))
        for (index in (suppressed.size - 1) downTo 0) {
            pending.add(ExceptionTraceTask(suppressed[index], "Suppressed", task.depth + 1))
        }
    }
    return text.toString()
}

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
