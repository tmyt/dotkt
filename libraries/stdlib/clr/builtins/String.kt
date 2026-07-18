/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM `actual`; bodies are `TODO` pending the `@Clr`/BCL
// binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin

/**
 * The `String` class represents character strings. All string literals in Kotlin programs, such as `"abc"`, are
 * implemented as instances of this class.
 */
// @ClrTypeAlias to System.String: kotlin.String *is* System.String, so the class is substituted away (NOT emitted) in
// the runtime/app assemblies. Without it the class was emitted with unbound `equals(Any?)`/`toString(): String` members
// whose signatures clash with System.Object.Equals/ToString -> type-load failure. The members below carry @ClrIntrinsic
// CALL-substitute metadata (resolved against System.String, not the stripped class); subSequence needs the exclusive-end
// -> length adaptation so it gets a real (rule-3) body delegating to the substring extension.
@kotlin.clr.ClrTypeAlias("System.String")
public actual class String : Comparable<String>, CharSequence {
    public actual companion object {}

    /**
     * Returns a string obtained by concatenating this string with the string representation of the given [other] object.
     */
    @kotlin.internal.IntrinsicConstEvaluation
    public actual operator fun plus(other: Any?): String = TODO("clr binding should be implemented")

    @kotlin.internal.IntrinsicConstEvaluation
    @kotlin.clr.ClrIntrinsic("Length")
    public actual override val length: Int get() = TODO("clr binding should be implemented")

    /**
     * Returns the character of this string at the specified [index].
     *
     * If the [index] is out of bounds of this string, throws an [IndexOutOfBoundsException].
     */
    @kotlin.internal.IntrinsicConstEvaluation
    @kotlin.clr.ClrIntrinsic("get_Chars")
    public actual override fun get(index: Int): Char = TODO("clr binding should be implemented")

    // No 1:1 BCL member: Kotlin's end index is EXCLUSIVE while System.String.Substring(start, length) takes a length.
    // Rule-3 real body delegating to the substring extension (which adapts end -> length via nativeSubstring).
    public actual override fun subSequence(startIndex: Int, endIndex: Int): CharSequence = substring(startIndex, endIndex)

    // No @ClrIntrinsic: System.String.CompareTo(String) is CULTURE-sensitive (StringComparison.CurrentCulture),
    // whereas Kotlin/JVM String.compareTo is ORDINAL (lexicographic by UTF-16 code unit). A rule-3 real body does the
    // ordinal comparison using only the intrinsic siblings (length / get) + primitive Char arithmetic, matching JVM.
    @kotlin.internal.IntrinsicConstEvaluation
    public actual override fun compareTo(other: String): Int {
        val thisLength = this.length
        val otherLength = other.length
        val minLength = if (thisLength < otherLength) thisLength else otherLength
        var i = 0
        while (i < minLength) {
            val a = this[i]
            val b = other[i]
            if (a != b) return a.code - b.code
            i++
        }
        return thisLength - otherLength
    }

    // No @ClrIntrinsic: System.String.GetHashCode is RANDOMIZED per process (non-deterministic across runs),
    // whereas Kotlin/JVM contracts a DETERMINISTIC polynomial hash `s[0]*31^(n-1) + ... + s[n-1]` (0 for the
    // empty string). Rule-3 real body over the intrinsic siblings (length / get) + primitive Char/Int arithmetic;
    // the `* 31` and `+` wrap on Int overflow (unchecked), matching JVM.
    // A `.hashCode()` call on a `String` receiver dispatches to THIS deterministic body: kotc's universal-method
    // routing falls through to the declared override when the receiver TYPE declares its own routable hashCode
    // (BirEmitterCalls.kt `fallThrough = declaresOwn` gate). So DotKt `String.hashCode()` is deterministic and
    // matches Kotlin/JVM's polynomial contract across runs — a stable interop guarantee (unlike
    // System.String.GetHashCode, which is per-process randomized).
    public actual override fun hashCode(): Int {
        var h = 0
        val n = this.length
        var i = 0
        while (i < n) {
            h = h * 31 + this[i].code
            i++
        }
        return h
    }

    /**
     * Indicates if [other] object is equal to this [String].
     */
    @kotlin.internal.IntrinsicConstEvaluation
    @kotlin.clr.ClrIntrinsic("Equals")
    public actual override fun equals(other: Any?): Boolean = TODO("clr binding should be implemented")

    @kotlin.internal.IntrinsicConstEvaluation
    @kotlin.clr.ClrIntrinsic("ToString")
    public actual override fun toString(): String = TODO("clr binding should be implemented")
}
