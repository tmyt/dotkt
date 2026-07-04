@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM actual; bodies are TODO pending the @Clr/BCL binding step
// (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.io

// The BCL console. Console.Write/WriteLine(object) renders a NON-null value via its own ToString, but a null
// object writes an EMPTY line — Kotlin's print/println(Any?) render null as the string "null". So the public
// functions coalesce the argument to "null" with a plain elvis (NO forced toString — the BCL call handles a
// non-null value) and delegate to a private @ClrIntrinsic helper (a BCL call replaces the helper's body). The
// null-rendering is thus ordinary Kotlin stdlib code, not a per-method compiler injection; a consumer references
// the stdlib to resolve print/println, exactly as for any other stdlib function.
@kotlin.clr.ClrIntrinsic("System.Console.Write")
internal fun clrConsoleWrite(value: Any): Unit = TODO("@ClrIntrinsic System.Console.Write")
@kotlin.clr.ClrIntrinsic("System.Console.WriteLine")
internal fun clrConsoleWriteLine(value: Any): Unit = TODO("@ClrIntrinsic System.Console.WriteLine")

/** Prints the given [message] to the standard output stream. */
public actual fun print(message: Any?) { clrConsoleWrite(message ?: "null") }

/** Prints the given [message] and the line separator to the standard output stream. */
public actual fun println(message: Any?) { clrConsoleWriteLine(message ?: "null") }

/** Prints the line separator to the standard output stream. */
@kotlin.clr.ClrIntrinsic("System.Console.WriteLine")
public actual fun println() { TODO("@ClrIntrinsic System.Console.WriteLine") }

/**
 * Reads a line of input from the standard input stream and returns it,
 * or throws a [RuntimeException] if EOF has already been reached when [readln] is called.
 *
 * LF or CRLF is treated as the line terminator. Line terminator is not included in the returned string.
 *
 * The input is decoded using the system default Charset. A [CharacterCodingException] is thrown if input is malformed.
 */
@SinceKotlin("1.6")
public actual fun readln(): String = readlnOrNull() ?: throw ReadAfterEOFException("EOF has already been reached")

/**
 * Reads a line of input from the standard input stream and returns it,
 * or return `null` if EOF has already been reached when [readlnOrNull] is called.
 *
 * LF or CRLF is treated as the line terminator. Line terminator is not included in the returned string.
 *
 * The input is decoded using the system default Charset. A [CharacterCodingException] is thrown if input is malformed.
 */
// @ClrIntrinsic-bound: System.Console.ReadLine returns the next line, or null at end-of-stream (matches readlnOrNull's contract).
@SinceKotlin("1.6")
@kotlin.clr.ClrIntrinsic("System.Console.ReadLine")
public actual fun readlnOrNull(): String? = TODO("@ClrIntrinsic System.Console.ReadLine")
