@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM actual; bodies are TODO pending the @Clr/BCL binding step
// (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin

/**
 * Creates a Char with the specified [code].
 *
 * @sample samples.text.Chars.charFromCode
 */
@SinceKotlin("1.5")
@kotlin.internal.InlineOnly
public actual inline fun Char(code: UShort): Char = TODO("clr binding should be implemented")
