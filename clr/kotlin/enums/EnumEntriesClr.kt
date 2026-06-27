@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM actual; bodies are TODO pending the @Clr/BCL binding step
// (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.enums

@SinceKotlin("1.9")
@PublishedApi
@kotlin.internal.InlineOnly
internal actual inline fun <T : Enum<T>> enumEntriesIntrinsic(): EnumEntries<T> =
    TODO("clr binding should be implemented")
