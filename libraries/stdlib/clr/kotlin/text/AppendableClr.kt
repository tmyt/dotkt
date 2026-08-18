@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.text

// `Appendable` is a JVM-shaped abstraction with no distinct .NET representation: the only CLR "appendable char sink"
// is System.Text.StringBuilder (the sole CLR implementer of this interface — see StringBuilderClr.kt). Per the project
// philosophy (Kotlin carries JVM accidental complexity; on the CLR, identify and discard it — clr-not-jvm-discard-jvmisms),
// and mirroring the CharSequence->System.String collapse (docs/design-charsequence-clr-string.md), DotKt models
// `Appendable` as `System.Text.StringBuilder` at the CLR boundary. bir2cir reads this @ClrTypeAlias from the ref.dll and
// lowers every `Appendable` token (including the generic bound `A : Appendable` on joinTo/joinToString) to the BCL type,
// so the constraint `A : Appendable` becomes the satisfiable `A : System.Text.StringBuilder` and the type argument
// StringBuilder (itself @ClrTypeAlias("System.Text.StringBuilder")) no longer violates it. `append(Char)`/
// `append(CharSequence?)` carry @ClrIntrinsic("Append") so a call through a generic `A : Appendable` receiver routes to
// System.Text.StringBuilder.Append (CharSequence lowers to string, matching Append(char)/Append(string?)). The range
// overload carries the same binding plus @ClrCountFromExclusiveEnd on its end index, so bir2cir adapts Kotlin's
// exclusive end to the BCL overload's count without re-evaluating either argument.
//
// The KDoc contract "if [value] is null, the four characters `null` are appended" is implemented at the physical BCL
// boundary: CharSequence arguments are snapshotted through the null-safe Any?.toString() bridge before Append.
@kotlin.clr.ClrTypeAlias("System.Text.StringBuilder")
public actual interface Appendable {
    @kotlin.clr.ClrIntrinsic("Append")
    public actual fun append(value: Char): Appendable
    @kotlin.clr.ClrIntrinsic("Append")
    public actual fun append(value: CharSequence?): Appendable
    @kotlin.clr.ClrIntrinsic("Append")
    public actual fun append(
        value: CharSequence?,
        startIndex: Int,
        @kotlin.clr.ClrCountFromExclusiveEnd(startIndex = 1) endIndex: Int,
    ): Appendable
}
