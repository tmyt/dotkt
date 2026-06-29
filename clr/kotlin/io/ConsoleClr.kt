@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM actual; bodies are TODO pending the @Clr/BCL binding step
// (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.io

import clr.Clr

// @Clr-bound to the BCL console (System.Console.Write/WriteLine). NOT inline: a BCL call replaces the body, and the
// BCL internals are native — there is nothing to inline. The TODO body never runs (the @Clr call-site routing wins).
/** Prints the given [message] to the standard output stream. */
@Clr("System.Console.Write")
public actual fun print(message: Any?) { TODO("@Clr System.Console.Write") }

/** Prints the given [message] and the line separator to the standard output stream. */
@Clr("System.Console.WriteLine")
public actual fun println(message: Any?) { TODO("@Clr System.Console.WriteLine") }

/** Prints the line separator to the standard output stream. */
@Clr("System.Console.WriteLine")
public actual fun println() { TODO("@Clr System.Console.WriteLine") }

/**
 * Reads a line of input from the standard input stream and returns it,
 * or throws a [RuntimeException] if EOF has already been reached when [readln] is called.
 *
 * LF or CRLF is treated as the line terminator. Line terminator is not included in the returned string.
 *
 * The input is decoded using the system default Charset. A [CharacterCodingException] is thrown if input is malformed.
 */
@SinceKotlin("1.6")
public actual fun readln(): String = TODO("clr binding should be implemented")

/**
 * Reads a line of input from the standard input stream and returns it,
 * or return `null` if EOF has already been reached when [readlnOrNull] is called.
 *
 * LF or CRLF is treated as the line terminator. Line terminator is not included in the returned string.
 *
 * The input is decoded using the system default Charset. A [CharacterCodingException] is thrown if input is malformed.
 */
@SinceKotlin("1.6")
public actual fun readlnOrNull(): String? = TODO("clr binding should be implemented")
