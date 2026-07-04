@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM actual; bodies are TODO pending the @Clr/BCL binding step
// (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.io

// The Any? overloads MUST render null as the string "null" (Kotlin semantics), so they render the message with
// `message?.toString() ?: "null"` (safe-call the member toString, never invoked on a null ref; null coalesces to the
// literal "null") and forward the resulting NON-NULL String to the STRING-typed Console intrinsics below. Binding
// print(Any?) directly to Console.Write(Object) would print an EMPTY line for null (the BCL renders a null object as
// ""), diverging from Kotlin — hence the Rule-3 body over an intrinsic sibling.
/** Prints the given [message] to the standard output stream. */
public actual fun print(message: Any?) { clrWrite(message?.toString() ?: "null") }

/** Prints the given [message] and the line separator to the standard output stream. */
public actual fun println(message: Any?) { clrWriteLine(message?.toString() ?: "null") }

/** Prints the line separator to the standard output stream. */
@kotlin.clr.ClrIntrinsic("System.Console.WriteLine")
public actual fun println() { TODO("@ClrIntrinsic System.Console.WriteLine") }

// STRING-typed console intrinsics. NOT inline: a BCL call replaces the body, the BCL internals are native — nothing to
// inline. The TODO bodies never run (the call-site substitution to System.Console.Write/WriteLine(String) wins).
@kotlin.clr.ClrIntrinsic("System.Console.Write")
private fun clrWrite(message: String): Unit { TODO("@ClrIntrinsic System.Console.Write") }

@kotlin.clr.ClrIntrinsic("System.Console.WriteLine")
private fun clrWriteLine(message: String): Unit { TODO("@ClrIntrinsic System.Console.WriteLine") }

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
