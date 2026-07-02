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
// System.Text.StringBuilder.Append (CharSequence lowers to string, matching Append(char)/Append(string?)).
@kotlin.clr.ClrTypeAlias("System.Text.StringBuilder")
public actual interface Appendable {
    @kotlin.clr.ClrIntrinsic("Append")
    public actual fun append(value: Char): Appendable
    @kotlin.clr.ClrIntrinsic("Append")
    public actual fun append(value: CharSequence?): Appendable
    // No @ClrIntrinsic: Kotlin's (value, startIndex, endIndex) has an EXCLUSIVE end index while .NET
    // StringBuilder.Append(string, int, int) takes a COUNT — a direct bind would be wrong. Unused by the joinTo/
    // joinToString path (appendElement only calls the 1-arg overloads); StringBuilder supplies its own bodied override.
    public actual fun append(value: CharSequence?, startIndex: Int, endIndex: Int): Appendable
}
