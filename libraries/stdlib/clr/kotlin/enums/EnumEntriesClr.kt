@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM actual; bodies are TODO pending the @Clr/BCL binding step
// (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.enums

// CALL-SITE INTERCEPTED (kotc): on the CLR all type args are reified, so every call to this intrinsic is lowered by
// BirEmitter (ENUM_REIFIED_INTRINSICS) to the same BIR nodes as `T.entries` — a rich enum's synthesized static
// `values()`, or the semantic `enumValues` node (System.Enum.GetValues) for a basic/generic-param T. This body is
// never invoked; it stays a filler so the rt-emitted `enumEntries<T>` keeps a valid `EnumEntries<T>`-typed body
// (interception is skipped under stdlibCompile — returning `T[]` where the interface is declared would be invalid IL).
// KNOWN GAP: a call reached through a non-inlined GENERIC context resolves via System.Enum reflection and therefore
// works for BASIC enums only (a rich enum lowers to a plain class).
@SinceKotlin("1.9")
@PublishedApi
@kotlin.internal.InlineOnly
internal actual inline fun <T : Enum<T>> enumEntriesIntrinsic(): EnumEntries<T> =
    TODO("clr binding should be implemented")
