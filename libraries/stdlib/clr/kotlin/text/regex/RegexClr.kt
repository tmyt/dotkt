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
@kotlin.clr.ClrTypeAlias("System.Text.RegularExpressions.Regex")
public actual class Regex {

    /** Creates a regular expression from the specified [pattern] string and the default options.  */
    public actual constructor(pattern: String)

    /** Creates a regular expression from the specified [pattern] string and the specified single [option].  */
    public actual constructor(pattern: String, option: RegexOption)

    /** Creates a regular expression from the specified [pattern] string and the specified set of [options].  */
    public actual constructor(pattern: String, options: Set<RegexOption>)

    /** The pattern string of this regular expression. */
    // Annotation-bug fix: System...Regex has no `Pattern` property; Regex.ToString() returns the pattern string.
    @kotlin.clr.ClrIntrinsic("ToString")
    public actual val pattern: String
        get() = TODO("clr binding should be implemented")

    /** The set of options that were used to create this regular expression.  */
    // TODO(clr): decode Regex.Options (System...RegexOptions [Flags] enum) -> Set<RegexOption>; needs a BCL enum->Int binding.
    public actual val options: Set<RegexOption>
        get() = TODO("clr binding should be implemented")

    /** Indicates whether the regular expression matches the entire [input]. */
    // Annotation-bug fix: dropped @ClrIntrinsic("IsMatch") — IsMatch is a *partial* match, but Kotlin `matches` is a full
    // (anchored) match. Realized as a full (anchored) match via [matchEntire] over the System...Match adapter.
    public actual infix fun matches(input: CharSequence): Boolean = matchEntire(input) != null

    /** Indicates whether the regular expression can find at least one match in the specified [input]. */
    @kotlin.clr.ClrIntrinsic("IsMatch")
    public actual fun containsMatchIn(input: CharSequence): Boolean = TODO("clr binding should be implemented")

    // First match at or after [startIndex], wrapped by the [ClrMatchResult] adapter over System...Match.
    @Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
    public actual fun find(input: CharSequence, startIndex: Int = 0): MatchResult? {
        val match = nativeMatch(input.toString(), startIndex)
        return if (match.success) ClrMatchResult(match) else null
    }

    // All matches as a lazy sequence, advancing via System...Match.NextMatch() (which also steps past zero-width matches).
    @Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
    public actual fun findAll(input: CharSequence, startIndex: Int = 0): Sequence<MatchResult> = TODO("clr binding should be implemented") // blocked: Sequence runtime (coroutine port) not yet wired

    // Anchored full match over the System...Match adapter: the leftmost match must start at 0 and span the whole input.
    // CLR deviation vs Kotlin/JVM: this does not re-anchor/force backtracking, so a pattern whose leftmost match is shorter
    // than the full input (e.g. an alternation `a|ab` over "ab") yields null even though an anchored full match exists.
    public actual fun matchEntire(input: CharSequence): MatchResult? {
        // CLR: `CharSequence` has no BCL length (System.String does NOT implement the synthetic CharSequence interface),
        // so materialize the input to a String ONCE and use its length — `input.length` on a String-at-runtime CharSequence
        // has no dispatchable slot.
        val text = input.toString()
        val match = nativeMatch(text)
        return if (match.success && match.index == 0 && match.length == text.length) ClrMatchResult(match) else null
    }

    // System...Regex.Match(input, index) searches forward from [index] over the full string (lookbehind transparent, `^`
    // not anchored to index) — a faithful realization of matchAt; require the match to start exactly at [index].
    @SinceKotlin("1.7")
    @WasExperimental(ExperimentalStdlibApi::class)
    public actual fun matchAt(input: CharSequence, index: Int): MatchResult? {
        val text = input.toString()   // CLR: use the String length (synthetic CharSequence has no dispatchable length on String)
        if (index < 0 || index > text.length) {
            throw IndexOutOfBoundsException("index out of bounds: $index, input length: ${text.length}")
        }
        val match = nativeMatch(text, index)
        return if (match.success && match.index == index) ClrMatchResult(match) else null
    }

    @SinceKotlin("1.7")
    @WasExperimental(ExperimentalStdlibApi::class)
    public actual fun matchesAt(input: CharSequence, index: Int): Boolean {
        val text = input.toString()   // CLR: use the String length (synthetic CharSequence has no dispatchable length on String)
        if (index < 0 || index > text.length) {
            throw IndexOutOfBoundsException("index out of bounds: $index, input length: ${text.length}")
        }
        val match = nativeMatch(text, index)
        return match.success && match.index == index
    }

    @kotlin.clr.ClrIntrinsic("Replace")
    public actual fun replace(input: CharSequence, replacement: String): String = TODO("clr binding should be implemented")

    // Walk the matches over the System...Match adapter, splicing in transform()'s result for each match (mirrors Kotlin/JVM;
    // avoids bridging a Kotlin lambda to a System...MatchEvaluator delegate).
    public actual fun replace(input: CharSequence, transform: (MatchResult) -> CharSequence): String {
        var match: MatchResult? = find(input, 0) ?: return input.toString()

        var lastStart = 0
        val length = input.length
        val sb = StringBuilder(length)
        do {
            val foundMatch = match!!
            sb.append(input, lastStart, foundMatch.range.start)
            sb.append(transform(foundMatch))
            lastStart = foundMatch.range.endInclusive + 1
            match = foundMatch.next()
        } while (lastStart < length && match != null)

        if (lastStart < length) {
            sb.append(input, lastStart, length)
        }

        return sb.toString()
    }

    // Replaces the first occurrence only: Regex.Replace(input, replacement, count = 1).
    public actual fun replaceFirst(input: CharSequence, replacement: String): String =
        nativeReplaceFirst(input, replacement, 1)

    // Thin wrapper for the count-limited overload Regex.Replace(string input, string replacement, int count).
    @kotlin.clr.ClrIntrinsic("Replace")
    private fun nativeReplaceFirst(input: CharSequence, replacement: String, count: Int): String =
        TODO("@Clr System.Text.RegularExpressions.Regex.Replace(string,string,int)")

    // Instance match entry points: System.Text.RegularExpressions.Regex.Match(string) / Match(string, int).
    @kotlin.clr.ClrIntrinsic("Match")
    private fun nativeMatch(input: String): ClrMatch = TODO("@Clr System.Text.RegularExpressions.Regex.Match(string)")

    @kotlin.clr.ClrIntrinsic("Match")
    private fun nativeMatch(input: String, startat: Int): ClrMatch = TODO("@Clr System.Text.RegularExpressions.Regex.Match(string,int)")

    // Split around matches by walking the System...Match adapter, honoring Kotlin `limit` (0 = unlimited) semantics.
    @Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
    public actual fun split(input: CharSequence, limit: Int = 0): List<String> {
        requireNonNegativeLimit(limit)

        var match = nativeMatch(input.toString())
        if (limit == 1 || !match.success) return listOf(input.toString())

        val result = ArrayList<String>(if (limit > 0) limit.coerceAtMost(10) else 10)
        var lastStart = 0
        val lastSplit = limit - 1 // negative if there's no limit

        do {
            result.add(input.substring(lastStart, match.index))
            lastStart = match.index + match.length
            if (lastSplit >= 0 && result.size == lastSplit) break
            match = match.nextMatch()
        } while (match.success)

        result.add(input.substring(lastStart, input.length))

        return result
    }

    // CLR realization: eager [split] result viewed as a sequence (same elements; not lazy unlike Kotlin/JVM).
    @SinceKotlin("1.6")
    @Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
    public actual fun splitToSequence(input: CharSequence, limit: Int = 0): Sequence<String> = TODO("clr binding should be implemented") // blocked: Sequence runtime not yet wired

    @kotlin.clr.ClrIntrinsic("ToString")
    public override fun toString(): String = TODO("clr binding should be implemented")

    public actual companion object {
        /**
         * Returns a regular expression that matches the specified [literal] string literally.
         * No characters of that string will have special meaning when searching for an occurrence of the regular expression.
         */
        public actual fun fromLiteral(literal: String): Regex = Regex(escape(literal))

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
        // In .NET replacement strings only `$` is special (e.g. `$1`, `$$`); escape it to `$$` for a literal replacement.
        public actual fun escapeReplacement(literal: String): String = literal.replace("\$", "\$\$", false)
    }
}

// === System.Text.RegularExpressions adapters ===
// These @ClrIntrinsic classes ARE the BCL types (Match/Group/GroupCollection); their members are metadata-only TODO stubs
// bound to BCL members (properties → bare name, methods/indexers → accessor name). The BODY methods of [Regex] above build
// the Kotlin `MatchResult`/`MatchGroupCollection` surface over them via the [ClrMatchResult]/[ClrMatchGroupCollection]
// adapter classes (which ARE emitted as real Kotlin classes).

/** Binds System.Text.RegularExpressions.Capture/Group members used by a captured group. */
@kotlin.clr.ClrTypeAlias("System.Text.RegularExpressions.Group")
internal class ClrGroup {
    @kotlin.clr.ClrIntrinsic("Success")
    val success: Boolean get() = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Value")
    val value: String get() = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Index")
    val index: Int get() = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Length")
    val length: Int get() = TODO("clr binding should be implemented")
}

/** Binds System.Text.RegularExpressions.GroupCollection (Count + the by-index and by-name `this[..]` indexers). */
@kotlin.clr.ClrTypeAlias("System.Text.RegularExpressions.GroupCollection")
internal class ClrGroupCollection {
    @kotlin.clr.ClrIntrinsic("Count")
    val count: Int get() = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("get_Item")
    operator fun get(index: Int): ClrGroup = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("get_Item")
    operator fun get(name: String): ClrGroup = TODO("clr binding should be implemented")
}

/** Binds System.Text.RegularExpressions.Match (inherits Value/Index/Length/Success; adds Groups + NextMatch). */
@kotlin.clr.ClrTypeAlias("System.Text.RegularExpressions.Match")
internal class ClrMatch {
    @kotlin.clr.ClrIntrinsic("Success")
    val success: Boolean get() = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Value")
    val value: String get() = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Index")
    val index: Int get() = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Length")
    val length: Int get() = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Groups")
    val groups: ClrGroupCollection get() = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("NextMatch")
    fun nextMatch(): ClrMatch = TODO("clr binding should be implemented")
}

/** Kotlin `MatchResult` over a System...Match. Real emitted class (not @ClrIntrinsic). */
internal class ClrMatchResult(private val nativeMatch: ClrMatch) : MatchResult {
    override val range: IntRange
        get() = nativeMatch.index until (nativeMatch.index + nativeMatch.length)
    override val value: String
        get() = nativeMatch.value
    // Lazy (getter, not an eager ctor field): building the group collection is only needed when `groups`/`groupValues`
    // are actually read, and doing it eagerly forced ClrMatchGroupCollection (: AbstractCollection) to load on EVERY
    // match — a needless dependency for the common value/range path.
    override val groups: MatchGroupCollection
        get() = ClrMatchGroupCollection(nativeMatch.groups)
    override val groupValues: List<String>
        get() {
            val g = nativeMatch.groups
            // .NET reports an unmatched optional group as Value == "" (Success == false), matching Kotlin's empty-string slot.
            return (0 until g.count).map { g[it].value }
        }

    // Override the default-interface getter explicitly: the inherited `MatchResult.destructured` default getter
    // (`get() = Destructured(this)`) InvalidProgram's when dispatched on the concrete adapter. A plain override on the
    // concrete class emits a normal method and constructs the same Destructured wrapper correctly.
    override val destructured: MatchResult.Destructured get() = MatchResult.Destructured(this)

    override fun next(): MatchResult? {
        // System...Match.NextMatch() advances to the next match (stepping past zero-width matches), so no manual index math.
        val m = nativeMatch.nextMatch()
        return if (m.success) ClrMatchResult(m) else null
    }
}

/** Kotlin `MatchNamedGroupCollection` over a System...GroupCollection. Real emitted class (not @ClrIntrinsic).
 *
 * Implements `MatchNamedGroupCollection` (: Collection<MatchGroup?>) DIRECTLY — the Collection members (contains/
 * containsAll/isEmpty) are spelled out here rather than inherited from `AbstractCollection`. The abstract-generic
 * base was fragile: constructing `AbstractCollection<MatchGroup?>` as ClrMatchGroupCollection's base failed to
 * type-load at runtime (`Could not load type kotlin.collections.AbstractCollection`1`) when `.groups` was first
 * read — and without `contains`, a `group in match.groups` check would have no member to dispatch. A direct
 * implementation (like ClrMatchResult/ClrSubList) gets the same @Clr-collection reverse GetEnumerator bridge with
 * no dependency on the abstract base. */
internal class ClrMatchGroupCollection(
    private val nativeGroups: ClrGroupCollection
) : MatchNamedGroupCollection {
    override val size: Int get() = nativeGroups.count
    override fun isEmpty(): Boolean = nativeGroups.count == 0

    // `group in match.groups` dispatches here. Linear scan (the collection is tiny — groupCount + 1).
    override fun contains(element: MatchGroup?): Boolean {
        val n = nativeGroups.count
        var i = 0
        while (i < n) { if (get(i) == element) return true; i++ }
        return false
    }

    override fun containsAll(elements: Collection<MatchGroup?>): Boolean {
        for (e in elements) if (!contains(e)) return false
        return true
    }

    override fun iterator(): Iterator<MatchGroup?> = object : Iterator<MatchGroup?> {
        private var i = 0
        override fun hasNext(): Boolean = i < size
        override fun next(): MatchGroup? = get(i++)
    }

    override fun get(index: Int): MatchGroup? {
        val g = nativeGroups[index]
        return if (g.success) MatchGroup(g.value, g.index until (g.index + g.length)) else null
    }

    override fun get(name: String): MatchGroup? {
        val g = nativeGroups[name]
        return if (g.success) MatchGroup(g.value, g.index until (g.index + g.length)) else null
    }
}
