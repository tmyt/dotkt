/*
 * Copyright 2010-2021 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM `actual`; bodies are `TODO` pending the `@Clr`/BCL
// binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.text

/**
 * Provides enumeration values to use to set regular expression options.
 */
public actual enum class RegexOption {
    /** Enables case-insensitive matching. Case comparison is Unicode-aware. */
    IGNORE_CASE,

    /** Enables multiline mode.
     *
     * In multiline mode the expressions `^` and `$` match just after or just before,
     * respectively, a line terminator or the end of the input sequence. */
    MULTILINE,

    /** Enables literal parsing of the pattern.
     *
     * Metacharacters or escape sequences in the input sequence will be given no special meaning.
     */
    LITERAL,

    /** Enables Unix lines mode. In this mode, only the `'\n'` is recognized as a line terminator. */
    UNIX_LINES,

    /** Permits whitespace and comments in pattern. */
    COMMENTS,

    /** Enables the mode, when the expression `.` matches any character, including a line terminator. */
    DOT_MATCHES_ALL,

    /** Enables equivalence by canonical decomposition. */
    CANON_EQ
}


/**
 * Represents the results from a single capturing group within a [MatchResult] of [Regex].
 *
 * @param value The value of captured group.
 * @param range The range of indices in the input string where group was captured.
 */
public actual data class MatchGroup(public actual val value: String, public val range: IntRange)

/**
 * Represents a compiled regular expression.
 * Provides functions to match strings in text with a pattern, replace the found occurrences and split text around matches.
 */
@Suppress("NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS") // Counterpart for @Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
@kotlin.clr.ClrIntrinsic("System.Text.RegularExpressions.Regex")
public actual class Regex {

    /** Creates a regular expression from the specified [pattern] string and the default options.  */
    public actual constructor(pattern: String)

    /** Creates a regular expression from the specified [pattern] string and the specified single [option].  */
    public actual constructor(pattern: String, option: RegexOption)

    /** Creates a regular expression from the specified [pattern] string and the specified set of [options].  */
    public actual constructor(pattern: String, options: Set<RegexOption>)

    /** The pattern string of this regular expression. */
    @kotlin.clr.ClrIntrinsic("Pattern")
    public actual val pattern: String
        get() = TODO("clr binding should be implemented")

    /** The set of options that were used to create this regular expression.  */
    public actual val options: Set<RegexOption>
        get() = TODO("clr binding should be implemented")

    /** Indicates whether the regular expression matches the entire [input]. */
    @kotlin.clr.ClrIntrinsic("IsMatch")
    public actual infix fun matches(input: CharSequence): Boolean = TODO("clr binding should be implemented")

    /** Indicates whether the regular expression can find at least one match in the specified [input]. */
    @kotlin.clr.ClrIntrinsic("IsMatch")
    public actual fun containsMatchIn(input: CharSequence): Boolean = TODO("clr binding should be implemented")

    @Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
    public actual fun find(input: CharSequence, startIndex: Int = 0): MatchResult? = TODO("clr binding should be implemented")

    @Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
    public actual fun findAll(input: CharSequence, startIndex: Int = 0): Sequence<MatchResult> { TODO("clr binding should be implemented") }

    public actual fun matchEntire(input: CharSequence): MatchResult? = TODO("clr binding should be implemented")

    @SinceKotlin("1.7")
    @WasExperimental(ExperimentalStdlibApi::class)
    public actual fun matchAt(input: CharSequence, index: Int): MatchResult? = TODO("clr binding should be implemented")

    @SinceKotlin("1.7")
    @WasExperimental(ExperimentalStdlibApi::class)
    public actual fun matchesAt(input: CharSequence, index: Int): Boolean = TODO("clr binding should be implemented")

    @kotlin.clr.ClrIntrinsic("Replace")
    public actual fun replace(input: CharSequence, replacement: String): String = TODO("clr binding should be implemented")

    public actual fun replace(input: CharSequence, transform: (MatchResult) -> CharSequence): String { TODO("clr binding should be implemented") }

    public actual fun replaceFirst(input: CharSequence, replacement: String): String = TODO("clr binding should be implemented")

    @Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
    public actual fun split(input: CharSequence, limit: Int = 0): List<String> { TODO("clr binding should be implemented") }

    @SinceKotlin("1.6")
    @Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
    public actual fun splitToSequence(input: CharSequence, limit: Int = 0): Sequence<String> { TODO("clr binding should be implemented") }

    @kotlin.clr.ClrIntrinsic("ToString")
    public override fun toString(): String = TODO("clr binding should be implemented")

    public actual companion object {
        /**
         * Returns a regular expression that matches the specified [literal] string literally.
         * No characters of that string will have special meaning when searching for an occurrence of the regular expression.
         */
        public actual fun fromLiteral(literal: String): Regex = TODO("clr binding should be implemented")

        /**
         * Returns a regular expression pattern string that matches the specified [literal] string literally.
         * No characters of that string will have special meaning when searching for an occurrence of the regular expression.
         */
        @kotlin.clr.ClrIntrinsic("Escape")
        public actual fun escape(literal: String): String = TODO("clr binding should be implemented")

        /**
         * Returns a literal replacement expression for the specified [literal] string.
         * No characters of that string will have special meaning when it is used as a replacement string in [Regex.replace] function.
         */
        public actual fun escapeReplacement(literal: String): String = TODO("clr binding should be implemented")
    }
}
